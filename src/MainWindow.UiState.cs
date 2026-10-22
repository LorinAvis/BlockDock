using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlockDock;
public partial class MainWindow
{
    readonly ListView transferList=new();readonly TextBlock taskBadge=new(){Text="0",Margin=new(7,0,0,0)};readonly Dictionary<string,(Border Root,TextBlock Label,ProgressBar Progress,Button Retry)> loading=new();
    void BuildTopBar(Grid shell)
    {
        var top=Cols(new(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto);top.Margin=new(24,7,12,4);var drag=new Border{Background=Brushes.Transparent,Child=Text("BlockDock",12)};drag.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==2)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else DragMove();};Row(top,drag,0);
        var task=Button("",(_,_)=>Navigate("tasks"));task.Content=H(new TextBlock{Text="\uE896",FontFamily=new("Segoe MDL2 Assets"),FontSize=17},taskBadge);task.ToolTip="下载任务 · 速度、文件和保存位置";Row(top,task,0,1);
        headerAccount.Text="未登录微软账号";headerAccount.VerticalAlignment=VerticalAlignment.Center;var account=Button("",(_,_)=>Navigate("account"));account.Content=H(headerAvatar,headerAccount);account.Padding=new(10,5,10,5);Row(top,account,0,2);Row(top,H(Button("—",(_,_)=>WindowState=WindowState.Minimized),Button("□",(_,_)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized),Button("×",(_,_)=>Close())),0,3);Row(shell,top,0,1);
    }
    void UpdateTaskBadge()=>taskBadge.Text=jobs.Count(x=>!x.Finished).ToString();
    Grid LoadingHost(FrameworkElement content,string key)
    {
        var grid=new Grid();grid.Children.Add(content);var label=Text("",15,true);label.TextAlignment=TextAlignment.Center;var bar=new ProgressBar{IsIndeterminate=true,Width=240,Height=5,Margin=new(0,18,0,14)};var retry=Button("重试",async(_,_)=>{switch(key){case "resource":await SearchResourcesAsync(0);break;case "minecraft":if(EnsureSource("minecraft"))await RefreshVersionsAsync();break;case "bedrock":if(EnsureSource("bedrock"))await RefreshBedrockAsync();break;case "installed":RefreshInstalled();break;}});retry.HorizontalAlignment=HorizontalAlignment.Center;
        var root=Card(V(label,bar,retry));root.MaxWidth=440;root.HorizontalAlignment=HorizontalAlignment.Center;root.VerticalAlignment=VerticalAlignment.Center;root.Visibility=Visibility.Collapsed;grid.Children.Add(root);loading[key]=(root,label,bar,retry);return grid;
    }
    void LoadState(string key,string state,string message="")
    {
        if(!loading.TryGetValue(key,out var ui))return;ui.Root.Visibility=state=="none"?Visibility.Collapsed:Visibility.Visible;ui.Label.Text=message;ui.Progress.Visibility=state=="busy"?Visibility.Visible:Visibility.Collapsed;ui.Retry.Visibility=state is "error" or "source"?Visibility.Visible:Visibility.Collapsed;ui.Retry.Content=state=="source"?"添加下载源":"重试";
    }
    async Task InstallProjectAsync(ResourceProject project)
    {
        var target=project.Type=="modpack"?null:targetBox.SelectedItem as GameInstance;if(project.Type!="modpack"&&target==null){resourceStatus.Text="请先选择安装到的实例。";return;}if(project.Type=="modpack"&&!EnsureSource("minecraft"))return;
        try{resourceStatus.Text="正在匹配 "+project.Name+" 的兼容版本…";using var service=new ResourceService(settings);var choices=await service.VersionsAsync(project.Id,project.Type,target,default);var version=choices.FirstOrDefault(x=>x.Type=="release")??choices.FirstOrDefault();if(version==null){resourceStatus.Text="没有适合这个实例的版本。";return;}await RunAsync("安装 "+project.Name,async(p,ct)=>{using var resources=new ResourceService(settings){Log=Log};if(project.Type=="modpack"){var game=await resources.InstallPackAsync(version,CleanName(project.Name),p,ct);settings.LastGame=game.Id;}else await resources.InstallAsync(project,version,target!,p,ct);},scope:target==null?null:"content:"+target.GameDirectory,destination:target?.GameDirectory??Path.Combine(settings.JavaRoot,"versions"),project:project);}catch(Exception e){resourceStatus.Text=e.Message;}
    }
    UIElement WallpaperControls()
    {
        wallpaperFit.ItemsSource=new[]{"完整显示 · 保持比例","填满窗口 · 裁剪边缘","拉伸到窗口"};wallpaperFit.SelectedIndex=settings.WallpaperFit switch{"Fill"=>1,"Stretch"=>2,_=>0};var brightness=new Slider{Minimum=.2,Maximum=1,Value=Math.Clamp(settings.WallpaperBrightness,.2,1),Margin=new(0,8,0,8)};var panel=new Slider{Minimum=.25,Maximum=1,Value=Math.Clamp(settings.PanelOpacity,.25,1),Margin=new(0,8,0,8)};var bText=Hint("");var pText=Hint("");
        void Update(){bText.Text=$"壁纸亮度 · {brightness.Value:P0}";pText.Text=$"面板不透明度 · {panel.Value:P0}";settings.WallpaperBrightness=brightness.Value;settings.PanelOpacity=panel.Value;settings.WallpaperFit=wallpaperFit.SelectedIndex switch{1=>"Fill",2=>"Stretch",_=>"Fit"};ApplyWallpaperStyle();if(ready)settings.Save();}
        wallpaperFit.SelectionChanged+=(_,_)=>Update();brightness.ValueChanged+=(_,_)=>Update();panel.ValueChanged+=(_,_)=>Update();bText.Text=$"壁纸亮度 · {brightness.Value:P0}";pText.Text=$"面板不透明度 · {panel.Value:P0}";return V(Text("适配方式"),wallpaperFit,Gap(8),bText,brightness,pText,panel);
    }
    void ApplyWallpaperStyle()
    {
        bool enabled=wallpaperImage.Source!=null||wallpaperVideo.Source!=null;var stretch=settings.WallpaperFit switch{"Fill"=>Stretch.UniformToFill,"Stretch"=>Stretch.Fill,_=>Stretch.Uniform};wallpaperImage.Stretch=wallpaperVideo.Stretch=stretch;wallpaperImage.Opacity=wallpaperVideo.Opacity=Math.Clamp(settings.WallpaperBrightness,.2,1);
        byte alpha=enabled?(byte)(255*Math.Clamp(settings.PanelOpacity,.25,1)):(byte)255;Resources["CardBackground"]=new SolidColorBrush(Color.FromArgb(alpha,18,29,39));Resources["ShellBackground"]=new SolidColorBrush(Color.FromArgb(enabled?(byte)Math.Max(170,(int)alpha):(byte)255,13,23,31));
    }
}
