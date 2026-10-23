using System.Windows;
using System.Windows.Controls;

namespace BlockDock;

public partial class MainWindow
{
    readonly LauncherUpdates launcherUpdates = new();
    readonly CancellationTokenSource updateCts = new();
    readonly Button updateNotice = new() { Visibility = Visibility.Collapsed };
    readonly Button checkUpdateButton = new(), downloadUpdateButton = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock updateSummary = new(), updateDetail = new();
    readonly CheckBox automaticUpdates = new(), previewUpdates = new();
    Border updateCard = null!;
    LauncherRelease? availableUpdate;
    bool checkingUpdate;

    UIElement BuildUpdateConfiguration()
    {
        automaticUpdates.Content = "启动时检查更新";
        automaticUpdates.IsChecked = settings.CheckUpdatesOnStartup;
        automaticUpdates.Foreground = Theme.Text;
        previewUpdates.Content = "接收预览版更新";
        previewUpdates.IsChecked = settings.IncludePreviewUpdates;
        previewUpdates.Foreground = Theme.Text;
        automaticUpdates.Click += (_, _) => { settings.CheckUpdatesOnStartup = automaticUpdates.IsChecked == true; settings.Save(); };
        previewUpdates.Click += async (_, _) => { settings.IncludePreviewUpdates = previewUpdates.IsChecked == true; settings.Save(); await CheckLauncherUpdateAsync(false); };
        Primary(checkUpdateButton, "检查更新", async (_, _) => await CheckLauncherUpdateAsync(true));
        downloadUpdateButton.Content = "下载新版 ↗";
        downloadUpdateButton.Click += (_, _) => { if (availableUpdate is { } release && LauncherUpdates.ValidPage(release.PageUrl)) Open(release.PageUrl); };
        updateNotice.Content = "有新版本";
        updateNotice.ToolTip = "查看 BlockDock 更新";
        updateNotice.Foreground = Theme.Accent;
        updateNotice.Click += (_, _) => { Navigate("settings"); Dispatcher.BeginInvoke(() => updateCard.BringIntoView()); };
        updateSummary.Text = "当前版本 · " + LauncherVersion.Label;
        updateSummary.TextWrapping = updateDetail.TextWrapping = TextWrapping.Wrap;
        updateDetail.Foreground = Theme.Muted;
        updateDetail.Text = "点击检查更新，获取最新版本。";
        updateCard = Card(V(Text("启动器更新", 19, true), Gap(8), updateSummary, Gap(6), updateDetail,
            Gap(), automaticUpdates, Gap(10), previewUpdates, Gap(), H(checkUpdateButton, downloadUpdateButton)));
        Loaded += async (_, _) => { if (settings.CheckUpdatesOnStartup) await CheckLauncherUpdateAsync(false); };
        Closed += (_, _) => { updateCts.Cancel(); launcherUpdates.Dispose(); };
        return updateCard;
    }

    async Task CheckLauncherUpdateAsync(bool manual)
    {
        if (checkingUpdate || updateCts.IsCancellationRequested) return;
        checkingUpdate = true;
        checkUpdateButton.IsEnabled = previewUpdates.IsEnabled = false;
        checkUpdateButton.Content = "正在检查…";
        updateDetail.Text = "正在检查新版本…";
        try
        {
            var result = await launcherUpdates.CheckAsync(settings.IncludePreviewUpdates, manual, updateCts.Token);
            ApplyUpdateResult(result);
        }
        catch (OperationCanceledException) when (updateCts.IsCancellationRequested) { }
        finally
        {
            checkingUpdate = false;
            checkUpdateButton.IsEnabled = previewUpdates.IsEnabled = true;
            checkUpdateButton.Content = "检查更新";
        }
    }

    void ApplyUpdateResult(UpdateCheck result)
    {
        availableUpdate = result.Available;
        updateNotice.Visibility = downloadUpdateButton.Visibility = availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
        updateSummary.Text = availableUpdate is { } release ? "发现新版本 · " + release.Tag + (release.Preview ? " 预览版" : "") : "当前版本 · " + LauncherVersion.Label;
        string last = result.CheckedAt is { } date ? "上次检查：" + date.ToLocalTime().ToString("MM-dd HH:mm") + (result.Cached ? " · 缓存" : "") : "";
        updateDetail.Text = result.Error is { } error ? error + (last.Length > 0 ? "\n" + last : "")
            : (availableUpdate == null ? "已是所选更新通道的最新版本。" : "可前往发布页查看说明并下载 Windows 版。") + "\n" + last;
    }
}
