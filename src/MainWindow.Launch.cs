using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BlockDock;

public partial class MainWindow
{
    readonly TextBlock launchStatus=new(){Text="准备就绪",Foreground=Theme.Muted,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};readonly TextBlock launchElapsed=new(){Foreground=Theme.Muted,VerticalAlignment=VerticalAlignment.Center,Margin=new(10,0,10,0)};readonly ProgressBar launchProgress=new(){Height=3,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed};readonly Button launchCancel=new(){Content="取消准备",Visibility=Visibility.Collapsed,Padding=new(9,3,9,3)};
    CancellationTokenSource? launchCts,gameMonitorCts;Process? launchedGame;bool launchPreparing,launchCanCancel=true,launchWindowSeen;string launchedName="";TimeSpan? startupDuration;Stopwatch? launchClock;
    readonly Button launchReportButton = new() { Content = "查看错误报告", Visibility = Visibility.Collapsed, Padding = new(9, 2, 9, 2) };
    readonly Button previousReportButton = new() { Content = "上次游戏错误报告", Visibility = Visibility.Collapsed };
    LaunchDiagnostics? currentReportAttempt;
    LaunchErrorReport? lastLaunchReport;
    void NotifyStatus(string message,bool force=false){status.Text=message;if(force||!launchPreparing&&launchedGame==null){launchStatus.Text=message;launchStatus.Foreground=Theme.Muted;}}
    void BuildLaunchStatus(Grid frame)
    {
        var line=Cols(new(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto);line.Margin=new(24,0,22,7);Row(line,launchStatus,0);Row(line,launchElapsed,0,1);Row(line,launchCancel,0,2);Row(line,launchReportButton,0,3);Row(frame,line,1);Row(frame,launchProgress,2);launchCancel.Click+=(_,_)=>{if(launchCanCancel){launchCts?.Cancel();launchStatus.Text="正在取消启动准备…";launchCancel.IsEnabled=false;}};
        lastLaunchReport = LaunchErrorReport.ReadLast();
        if (lastLaunchReport != null) previousReportButton.Visibility = Visibility.Visible;
        launchReportButton.Click += OpenLaunchReport;
        previousReportButton.Click += OpenLaunchReport;
        Closed+=(_,_)=>{gameMonitorCts?.Cancel();launchCts?.Cancel();};
    }
    void OpenLaunchReport(object sender, RoutedEventArgs e)
    {
        if (lastLaunchReport != null) new LaunchErrorReportWindow(lastLaunchReport) { Owner = this }.ShowDialog();
    }

    async Task PublishLaunchReportAsync(LaunchDiagnostics attempt, string stage, DateTimeOffset? processStarted = null, int? exitCode = null, Exception? error = null)
    {
        var report = await Task.Run(() => attempt.Build(stage, processStarted, exitCode, error));
        if (!ReferenceEquals(currentReportAttempt, attempt) || Dispatcher.HasShutdownStarted) return;
        lastLaunchReport = report;
        launchReportButton.Visibility = previousReportButton.Visibility = Visibility.Visible;
        try
        {
            WorldFiles.NoLinks(LaunchErrorReport.LastFile);
            report.Save(LaunchErrorReport.LastFile);
            previousReportButton.ToolTip = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            previousReportButton.ToolTip = "报告可在本次打开期间查看和导出。";
            Log("无法保存最近的游戏错误报告：" + SkinAccounts.Redact(e.Message));
        }
    }
    bool FocusLaunchedGame(){if(launchedGame==null)return false;try{if(!launchedGame.HasExited){GameProcessMonitor.Focus(launchedGame);return true;}}catch(InvalidOperationException){}return false;}
    void RefreshLaunchButton()
    {
        bool running=launchedGame!=null;playButton.IsEnabled=!launchPreparing&&(running?launchWindowSeen:(editionBox.SelectedIndex==1?homeBedrock.Items.Count>0:homeGame.Items.Count>0));playButton.Content=launchPreparing?"正在启动…":running?(launchWindowSeen?"↗  返回游戏":"等待游戏窗口…"):editionBox.SelectedIndex==1?"▶  启动基岩版":"▶  启动 Java 版";
    }
    async Task RunLaunchAsync(LaunchDiagnostics attempt,string scope,Func<IProgress<WorkProgress>,CancellationToken,Task<Process>> start,Action<DateTimeOffset>? started=null)
    {
        if(worldOperationCts!=null||OwnedWindows.OfType<WorldMapWindow>().Any(w=>w.ReadingWorld)){NotifyStatus("请等待地图读取或存档操作完成，再启动游戏。",true);return;}
        if(launchPreparing||FocusLaunchedGame())return;
        string name = attempt.Name;
        currentReportAttempt = attempt;
        launchReportButton.Visibility = Visibility.Collapsed;
        launchStatus.ToolTip = null;
        launchCts?.Dispose();var cts=launchCts=new();launchPreparing=true;launchCanCancel=true;launchWindowSeen=false;startupDuration=null;launchedName=name;launchClock=Stopwatch.StartNew();launchStatus.Foreground=Theme.Text;launchStatus.Text="准备启动 "+name;launchElapsed.Text="";launchProgress.Value=0;launchProgress.IsIndeterminate=true;launchProgress.Visibility=Visibility.Visible;launchCancel.Visibility=Visibility.Visible;launchCancel.Content="取消准备";launchCancel.IsEnabled=true;RefreshLaunchButton();
        if(!jobScopes.TryGetValue(scope,out var gate))jobScopes[scope]=gate=new(1);bool held=false;
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(_,_)=>{if(launchPreparing)launchElapsed.Text="已等待 "+launchClock.Elapsed.TotalSeconds.ToString("0.0")+" 秒";};timer.Start();
        bool acceptingProgress = true;
        var progress = new Progress<WorkProgress>(value =>
        {
            if (!acceptingProgress || !ReferenceEquals(launchCts, cts)) return;
            attempt.LastStep = value.Message;
            launchStatus.Text = value.Message + (value.Total > 0 ? $" · {value.Done}/{value.Total}" :
                value.Size > 0 ? $" · {value.Bytes/1048576.0:0.0}/{value.Size/1048576.0:0.0} MB" : "");
            launchStatus.ToolTip = launchStatus.Text;
        });
        try
        {
            if(gate.CurrentCount==0)launchStatus.Text="等待相关游戏文件任务完成…";await gate.WaitAsync(cts.Token);held=true;var process=await start(progress,cts.Token);DateTimeOffset? actualStart=null;try{actualStart=new DateTimeOffset(process.StartTime.ToUniversalTime(),TimeSpan.Zero);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){Log("无法读取实际游戏启动时间："+e.Message);}launchedGame=process;launchCanCancel=false;launchCancel.Visibility=Visibility.Collapsed;launchStatus.Text=name+" · 进程已启动，等待游戏窗口…";gameMonitorCts?.Cancel();var monitor=gameMonitorCts=new();_=TrackGameAsync(process,monitor,attempt);try{if(actualStart is DateTimeOffset time)started?.Invoke(time);settings.Save();RefreshGames();}catch(Exception e)when(e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException){Log("游戏已启动，但启动记录保存未完成："+e.Message);}Log(name+" 游戏进程已启动，等待实际窗口。");
        }
        catch(OperationCanceledException) when(cts.IsCancellationRequested){launchClock.Stop();launchStatus.Text="已取消启动准备";launchElapsed.Text="";launchProgress.Visibility=Visibility.Collapsed;}
        catch(Exception e)
        {
            launchClock.Stop(); timer.Stop(); acceptingProgress = false;
            launchCancel.Visibility = launchProgress.Visibility = Visibility.Collapsed;
            launchStatus.Text = "启动未完成：" + SkinAccounts.Redact(e.Message);
            launchStatus.ToolTip = launchStatus.Text; launchStatus.Foreground = Theme.Brush("#EBC883"); launchElapsed.Text = "";
            Log(launchStatus.Text);
            await PublishLaunchReportAsync(attempt, "启动准备失败", error: e);
        }
        finally{acceptingProgress=false;timer.Stop();if(held)gate.Release();launchPreparing=false;launchCancel.Visibility=Visibility.Collapsed;RefreshLaunchButton();}
    }
    async Task TrackGameAsync(Process process,CancellationTokenSource monitor,LaunchDiagnostics attempt)
    {
        try
        {
            GameObservation? final = null;
            await GameProcessMonitor.ObserveAsync(process,observation=>
            {
                if(!ReferenceEquals(launchedGame,process) || !ReferenceEquals(currentReportAttempt,attempt))return;
                if(observation.Exited) final=observation;
                if(observation.WindowReady&&!launchWindowSeen){launchWindowSeen=true;launchClock?.Stop();startupDuration=launchClock?.Elapsed;launchProgress.IsIndeterminate=false;launchProgress.Value=100;launchStatus.Text=launchedName+" · 游戏窗口已出现";RefreshLaunchButton();Log(launchStatus.Text+"，用时 "+startupDuration?.TotalSeconds.ToString("0.0")+" 秒。");}
                launchElapsed.Text=(startupDuration is TimeSpan duration?"启动 "+duration.TotalSeconds.ToString("0.0")+" 秒 · ":"等待窗口 · ")+"运行 "+observation.Elapsed.ToString(@"hh\:mm\:ss");
                if(observation.Exited){launchClock?.Stop();launchStatus.Text=launchedName+(observation.WindowReady?" · 游戏已退出":" · 未显示窗口即退出")+"（代码 "+observation.ExitCode+"）";if(!observation.WindowReady||observation.ExitCode!=0)launchStatus.Foreground=Theme.Brush("#EBC883");launchProgress.Visibility=Visibility.Collapsed;launchElapsed.Text="本次运行 "+observation.Elapsed.ToString(@"hh\:mm\:ss");Log(launchStatus.Text);}
                else if(!observation.WindowReady&&observation.Elapsed.TotalSeconds>=60)launchStatus.Text=launchedName+" · 进程仍在运行，尚未检测到游戏窗口";
            },monitor.Token);
            if (final is { } exit && (!exit.WindowReady || exit.ExitCode != 0))
            {
                using var drain = CancellationTokenSource.CreateLinkedTokenSource(monitor.Token);
                drain.CancelAfter(TimeSpan.FromSeconds(1));
                try { await process.WaitForExitAsync(drain.Token); }
                catch (OperationCanceledException) when (!monitor.IsCancellationRequested) { }
                monitor.Token.ThrowIfCancellationRequested();
                await PublishLaunchReportAsync(attempt, exit.WindowReady ? "游戏运行中异常退出" : "游戏窗口出现前退出", exit.ProcessStarted, exit.ExitCode);
            }
        }
        catch(OperationCanceledException) when(monitor.IsCancellationRequested){}catch(Exception e){if(!monitor.IsCancellationRequested&&ReferenceEquals(currentReportAttempt,attempt)){launchStatus.Text="游戏状态读取未完成："+SkinAccounts.Redact(e.Message);Log(launchStatus.Text);await PublishLaunchReportAsync(attempt,"读取游戏状态失败",error:e);}}
        finally{if(ReferenceEquals(launchedGame,process)){launchedGame=null;RefreshLaunchButton();}if(ReferenceEquals(gameMonitorCts,monitor))gameMonitorCts=null;monitor.Dispose();process.Dispose();}
    }
    void LockLaunchDeployment(){launchCanCancel=false;launchCancel.IsEnabled=false;launchCancel.Content="Windows 安装中";}
}
