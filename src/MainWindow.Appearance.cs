using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlockDock;
public partial class MainWindow
{
    readonly Grid wallpaperLayer=new(){IsHitTestVisible=false,ClipToBounds=true};readonly Image wallpaperImage=new(){Stretch=Stretch.UniformToFill,Opacity=.8};readonly MediaElement wallpaperVideo=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Manual,Stretch=Stretch.UniformToFill,IsMuted=true,Volume=0,Opacity=.8};
    readonly TextBox wallpaperRoot=new();readonly ListView wallpaperList=new(){Height=210,DisplayMemberPath="Display"};readonly TextBlock wallpaperStatus=new(){TextWrapping=TextWrapping.Wrap,Foreground=Theme.Muted};
    UIElement BuildWallpaperSettings()
    {
        wallpaperRoot.Text=settings.WallpaperEngineRoot;wallpaperVideo.MediaEnded+=(_,_)=>{wallpaperVideo.Position=TimeSpan.Zero;wallpaperVideo.Play();};wallpaperVideo.MediaFailed+=(_,e)=>{wallpaperVideo.Stop();wallpaperStatus.Text="此视频无法播放，请选择 Windows 能解码的视频，或改用图片。";};StateChanged+=(_,_)=>{if(wallpaperVideo.Source!=null){if(WindowState==WindowState.Minimized)wallpaperVideo.Pause();else wallpaperVideo.Play();}};Closed+=(_,_)=>wallpaperVideo.Close();
        return V(Card(V(Text("界面背景",19,true),Hint("默认使用内置海边插画，也可选择自己的图片或静音视频。"),Gap(),H(Button("自定义图片 / 视频",(_,_)=>ChooseWallpaper()),Button("恢复默认",(_,_)=>SetWallpaper(""))),Gap(),WallpaperControls(),Gap(),wallpaperStatus)),Gap(),Card(V(Text("小红车 · 本机壁纸",19,true),Hint("读取 Wallpaper Engine 已下载的壁纸。只显示可直接使用的图片和视频壁纸。"),Gap(),wallpaperRoot,H(Button("选择壁纸目录",(_,_)=>{var d=new OpenFolderDialog{Title="选择 Wallpaper Engine 或 431960 壁纸目录"};if(d.ShowDialog()==true)wallpaperRoot.Text=d.FolderName;}),Button("读取本机壁纸",async(_,_)=>await ScanWallpapersAsync())),Gap(),wallpaperList,Gap(),Button("应用所选壁纸",(_,_)=>{if(wallpaperList.SelectedItem is LocalWallpaper w)SetWallpaper(w.File);},true))));
    }
    async Task ScanWallpapersAsync(){wallpaperStatus.Text="正在读取本机壁纸…";try{string root=wallpaperRoot.Text.Trim();var list=await Task.Run(()=>WallpaperLibrary.Scan(root));wallpaperList.ItemsSource=list;settings.WallpaperEngineRoot=root;settings.Save();wallpaperStatus.Text=list.Count>0?$"找到 {list.Count} 个本机壁纸，请选择后应用。":"未找到壁纸。请选择 Wallpaper Engine 安装目录或 workshop/content/431960 文件夹。";}catch(Exception e){wallpaperStatus.Text="读取失败："+e.Message;}}
    void ChooseWallpaper(){var d=new OpenFileDialog{Title="选择启动器背景",Filter="图片与视频|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv"};if(d.ShowDialog()==true)SetWallpaper(d.FileName);}
    void SetWallpaper(string file){try{if(file.Length>0&&file!=settings.WallpaperFile){settings.WallpaperFit="Fill";wallpaperFit.SelectedIndex=1;}ApplyWallpaper(file);settings.WallpaperFile=WallpaperLibrary.IsScenePreview(file)?"":file;settings.Save();}catch(Exception e){Error(e);}}
    void ApplyWallpaper(string file)
    {
        wallpaperVideo.Stop();wallpaperVideo.Source=null;wallpaperImage.Source=DefaultWallpaper.Image;
        if(file.Length>0&&WallpaperLibrary.IsScenePreview(file)){settings.WallpaperFile="";wallpaperStatus.Text="当前使用内置背景，请重新选择图片或视频。";ApplyWallpaperStyle();return;}
        if(file.Length==0){wallpaperStatus.Text="当前：内置默认背景";ApplyWallpaperStyle();return;}
        if(!File.Exists(file)){wallpaperStatus.Text="背景文件已移动，当前使用内置默认背景。";ApplyWallpaperStyle();return;}
        if(WallpaperLibrary.IsImage(file))wallpaperImage.Source=Theme.Image(file);else if(WallpaperLibrary.IsVideo(file)){wallpaperImage.Source=null;wallpaperVideo.Source=new Uri(Path.GetFullPath(file));wallpaperVideo.Play();}else throw new IOException("请选择图片或受支持的视频。");wallpaperStatus.Text="当前背景："+Path.GetFileName(file);ApplyWallpaperStyle();
    }

    void ApplyDownloadView()
    {
        foreach(FrameworkElement child in downloadDeck.Children)child.Visibility=Visibility.Collapsed;
        if(downloadView=="minecraft")minecraftPanel.Visibility=Visibility.Visible;else if(downloadView is "mod" or "shader" or "resourcepack" or "modpack"){resourcesPanel.Visibility=Visibility.Visible;resourceTargetRow.Visibility=downloadView=="modpack"?Visibility.Collapsed:Visibility.Visible;}else if(downloadView=="bedrock")pages["bedrock"].Visibility=Visibility.Visible;
        foreach(var tab in downloadTabs)tab.Value.Style=tab.Key==downloadView?(Style)FindResource("Primary"):(Style)FindResource(typeof(Button));
    }
    static string CountContent(GameInstance g){try{return Directory.Exists(Path.Combine(g.GameDirectory,"mods"))?Directory.EnumerateFiles(Path.Combine(g.GameDirectory,"mods"),"*.jar").Count()+" 个模组":"0 个模组";}catch(IOException){return "模组目录暂不可读";}catch(UnauthorizedAccessException){return "模组目录暂不可读";}}
    void RefreshSelectedGame(){var g=homeGame.SelectedItem as GameInstance;selectedCover.Source=editionBox.SelectedIndex==1?(homeBedrock.SelectedItem as BedrockPackage)?.Icon??InstanceIcons.Get("bedrock"):g?.Icon??InstanceIcons.Get("vanilla");selectedSummary.Text=editionBox.SelectedIndex==1?"基岩版 · Windows":g?.Name??"还没有游戏实例";gameInfo.Text=editionBox.SelectedIndex==1?"在底部选择基岩版实例；每个版本分别保存游戏数据。":g?.Summary??"到下载管理安装游戏，或在设置里选择已有游戏文件夹。";selectedFacts.Children.Clear();selectedFacts.Children.Add(Hint(editionBox.SelectedIndex==1?"在左侧 Bedrock Edition 管理独立实例，下载其他版本请进入资源下载。":g?.ContentSummary??"安装游戏后会显示模组与启动信息。"));UpdateMemoryHint();}
    void UpdateOverview(){homeSummary.Text=$"本机已识别 {games.Count} 个 Java 实例 · {BedrockNative.Packages().Count} 个基岩版安装包";sourceStatus.Text="游戏本体："+(DownloadProvider.Load()?.Name??"未配置")+"\n内容平台：Modrinth";headerAccount.Text=accountBox.SelectedItem switch{MicrosoftAccount a=>a.Name,_=>"未选择账号"};headerAvatar.Source=accountBox.SelectedItem is MicrosoftAccount player&&playerAvatars.TryGetValue(player.Uuid,out var avatar)?avatar:BrandIcon.Image;RenderOptions.SetBitmapScalingMode(headerAvatar,BitmapScalingMode.NearestNeighbor);RefreshLaunchButton();}
    int installedGeneration;
    async void RefreshInstalled()
    {
        if(!ready||refreshing)return;int generation=++installedGeneration;installedList.ItemsSource=null;if(installedGame.SelectedItem is not GameInstance game){installedStatus.Text="请先选择实例。";LoadState("installed","empty",installedStatus.Text);return;}
        string kind=installedKind.SelectedIndex switch{1=>"resourcepack",2=>"shader",_=>"mod"};LoadState("installed","busy","正在读取内容与图标…");try{var list=await Task.Run(()=>ResourceService.Installed(game,kind).Select(x=>{_=x.Icon;return x;}).ToList());if(generation!=installedGeneration)return;installedList.ItemsSource=list;installedStatus.Text=$"{game.Name} · 共 {list.Count} 项";LoadState("installed",list.Count==0?"empty":"none","这个实例还没有安装此类内容。");}catch(Exception e){if(generation==installedGeneration){installedStatus.Text=e.Message;LoadState("installed","error","读取未完成："+e.Message);}}
    }
    void ToggleInstalled(){if(installedList.SelectedItem is not InstalledResource item)return;try{ResourceService.Toggle(item);RefreshInstalled();}catch(Exception e){Error(e);}}
    void SaveProvider(){if(active!=null){NotifyStatus("请等待当前任务结束后更换下载源。");return;}try{if(sourceBox.SelectedIndex==1&&DownloadProvider.Load() is null)throw new IOException("请先导入下载源配置。");settings.Source=sourceBox.SelectedIndex==1?"Custom":"Official";settings.Save();UpdateOverview();NotifyStatus("下载来源已应用。");}catch(Exception e){Error(e);}}
}
