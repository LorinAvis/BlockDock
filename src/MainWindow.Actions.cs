using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlockDock;
public partial class MainWindow
{
    static string PathOf(string relative)=>Path.Combine(AppContext.BaseDirectory,relative);
    void Error(Exception e)=>MessageBox.Show(this,SkinAccounts.Redact(e.Message),"操作未完成",MessageBoxButton.OK,MessageBoxImage.Information);
    bool Confirm(string message)=>MessageBox.Show(this,message,"请确认",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK;
    void Open(string path){try{Paths.Open(path);}catch(Exception e){Error(e);}}
    void Navigate(string id)
    {
        if(id=="provider"){OpenSourceSettings();return;}if(id=="bedrock"){ShowDownload(id);return;}
        if(id=="download"&&!settings.DownloadSourceIntroductionSeen&&downloadView is "minecraft" or "bedrock"&&!(downloadView=="minecraft"?DownloadProvider.HasJava:DownloadProvider.HasBedrock)){OpenSourceSettings(downloadView);return;}
        foreach(var p in pages.Where(x=>x.Key!="bedrock"))p.Value.Visibility=p.Key==id?Visibility.Visible:Visibility.Collapsed;
        foreach(var b in nav){b.Value.Background=b.Key==ActiveSidebar(id)?Theme.Brush("#15392E"):Brushes.Transparent;b.Value.Foreground=b.Key==ActiveSidebar(id)?Theme.Accent:Theme.Text;}
        title.Text=id switch{"home"=>"欢迎回来","games"=>"Java 游戏管理","download"=>"下载资源","installed"=>"实例内容","tasks"=>"下载管理","bedrock-local"=>"基岩版游戏管理","account"=>"账号管理","settings"=>"设置",_=>"关于 BlockDock"};
        subtitle.Text=id switch{"home"=>"选择游戏，装上喜欢的内容，开始自己的冒险。","games"=>"你的版本、模组与存档，各自独立管理。","download"=>"游戏、模组、光影与整合包，统一下载和管理。","tasks"=>"查看下载进度、速度和保存位置。","account"=>"使用微软正版账号，管理角色与皮肤。","provider"=>"管理游戏文件的下载来源和资源平台。","settings"=>"调整启动、文件位置和外观。",_=>"By Players, For Players."};
        if(id=="download")ApplyDownloadView();if(id=="home")UpdateOverview();
        if(id=="installed")RefreshInstalled();if(id=="bedrock-local")RefreshBedrockPackages();
    }
    void RefreshGames()
    {
        refreshing=true;
        try{var memory=MemoryAllocation.Snapshot();string home=(homeGame.SelectedItem as GameInstance)?.Id??settings.LastGame,target=(targetBox.SelectedItem as GameInstance)?.Id??home,installed=(installedGame.SelectedItem as GameInstance)?.Id??home;games=LaunchCore.Scan(settings).Select(x=>x with{Favorite=settings.Favorites.Contains(x.Id),LastPlayed=settings.LastPlayed.TryGetValue(x.Id,out var time)?time:null,ContentSummary=CountContent(x)+" · "+(settings.MemoryMode=="Auto"?"自动 ":"")+MemoryAllocation.Format(MemoryAllocation.Resolve(settings,x,memory))+" 内存"}).ToList();homeGame.ItemsSource=games;targetBox.ItemsSource=games;installedGame.ItemsSource=games;homeGame.SelectedItem=games.FirstOrDefault(x=>x.Id==home)??games.FirstOrDefault();targetBox.SelectedItem=games.FirstOrDefault(x=>x.Id==target)??homeGame.SelectedItem;installedGame.SelectedItem=games.FirstOrDefault(x=>x.Id==installed)??homeGame.SelectedItem;gameCount.Text="共 "+games.Count+" 个实例";FilterGames();}
        finally{refreshing=false;}RefreshHomeBedrock();RefreshSelectedGame();RefreshInstalled();UpdateOverview();
    }
    void FilterGames(){var selected=(gameList.SelectedItem as GameInstance)?.Id;var query=games.Where(x=>x.Name.Contains(gameSearch.Text,StringComparison.OrdinalIgnoreCase)&&(instanceFilter=="all"||(instanceFilter=="favorite"?x.Favorite:x.Loader==instanceFilter)));gameList.ItemsSource=(instanceSort.SelectedIndex==1?query.OrderByDescending(x=>x.LastPlayed):query.OrderBy(x=>x.Name)).ToList();gameList.SelectedItem=games.FirstOrDefault(x=>x.Id==selected);foreach(var tab in instanceTabs)tab.Value.Style=tab.Key==instanceFilter?(Style)FindResource("Primary"):(Style)FindResource(typeof(Button));}
    void FilterMinecraft()
    {
        string filter=minecraftSearch.Text;var selected=(minecraftList.SelectedItem as GameVersion)?.Id;var list=versions.Where(x=>x.Id.Contains(filter,StringComparison.OrdinalIgnoreCase)&&(minecraftType.SelectedIndex==2||x.Type==(minecraftType.SelectedIndex==1?"snapshot":"release"))).ToList();minecraftList.ItemsSource=list;minecraftList.SelectedItem=list.FirstOrDefault(x=>x.Id==selected)??list.FirstOrDefault();
    }
    async Task RefreshVersionsAsync()
    {
        if(!EnsureSource("minecraft"))return;listCts?.Cancel();var cts=listCts=new();minecraftList.ItemsSource=null;installGameButton.IsEnabled=false;LoadState("minecraft","busy","正在读取 Java 版本…");
        try{using var dl=new Downloader(settings.Source){Log=Log};var result=await new JavaService(settings,dl).VersionsAsync(cts.Token);if(cts.IsCancellationRequested)return;versions=result;FilterMinecraft();LoadState("minecraft",versions.Count==0?"empty":"none","这个下载源还没有可用版本。");}catch(OperationCanceledException){}catch(Exception e){if(!cts.IsCancellationRequested)LoadState("minecraft","error","版本加载失败："+e.Message);}finally{if(listCts==cts)listCts=null;cts.Dispose();}
    }
    async void ShowDownload(string type)
    {
        if(type is "installed" or "tasks"){Navigate(type);return;}downloadView=type;if(type is "minecraft" or "mod" or "shader" or "resourcepack" or "modpack")resourceType=type;Navigate("download");if(pages["download"].Visibility!=Visibility.Visible)return;
        if(type=="minecraft"){if(!DownloadProvider.HasJava){minecraftList.ItemsSource=null;LoadState("minecraft","source","请先在设置里添加自己的 Java 下载源。");return;}if(versions.Count==0)await RefreshVersionsAsync();}
        else if(type=="bedrock"){if(!DownloadProvider.HasBedrock){bedrockList.ItemsSource=null;LoadState("bedrock","source","请先在设置里添加自己的基岩版下载源。");return;}if(bedrock.Count==0)await RefreshBedrockAsync();}
        else if(type is "mod" or "shader" or "resourcepack" or "modpack")await SearchResourcesAsync(0);
    }
    string CurrentLoader()=>loaderBox.SelectedIndex switch{1=>"fabric",2=>"forge",3=>"neoforge",_=>"vanilla"};
    async Task UpdateLoadersAsync()
    {
        if(!ready||minecraftList.SelectedItem is not GameVersion game)return;loaderCts?.Cancel();var cts=loaderCts=new();var loader=CurrentLoader();installGameButton.IsEnabled=false;loaderHint.Text="正在读取加载器版本…";instanceName.Text=game.Id+(loader=="vanilla"?" 原版":" "+loader);
        try{using var launch=new LaunchCore(settings);var choices=await launch.LoadersAsync(game.Id,loader,cts.Token);if(cts.IsCancellationRequested)return;loaderVersion.ItemsSource=choices;loaderVersion.SelectedItem=choices.FirstOrDefault(x=>x.Hint is "稳定版" or "推荐")??choices.FirstOrDefault();installGameButton.Content="安装 "+game.Id+(loader=="vanilla"?"":" · "+loader);loaderVersion.Visibility=loader=="vanilla"?Visibility.Collapsed:Visibility.Visible;loaderHint.Text=choices.Count==0?"这个游戏版本还没有对应加载器。":loader=="vanilla"?"适合原版游玩和材质包。":"安装时会同时准备游戏、Java 与加载器。";installGameButton.IsEnabled=choices.Count>0;}
        catch(OperationCanceledException){}catch(Exception e){if(!cts.IsCancellationRequested)loaderHint.Text=e.Message;}finally{if(loaderCts==cts)loaderCts=null;cts.Dispose();}
    }
    async Task InstallMinecraftAsync()
    {
        if(!EnsureSource("minecraft")||minecraftList.SelectedItem is not GameVersion version)return;string loader=CurrentLoader(),loaderId=(loaderVersion.SelectedItem as LoaderChoice)?.Version??"",name=instanceName.Text;
        await RunAsync("安装 Minecraft "+version.Id,async(p,ct)=>{using var launch=new LaunchCore(settings){Log=Log};var game=await launch.InstallAsync(version,loader,loaderId,name,p,ct);settings.LastGame=game.Id;Log("已准备好："+game.Name);});
    }
    async Task SearchResourcesAsync(int offset)
    {
        if(downloadView is not("mod" or "shader" or "resourcepack" or "modpack")||!ready)return;
        resourceCts?.Cancel();var cts=resourceCts=new();string type=resourceType,query=resourceSearch.Text;var target=type=="modpack"?null:targetBox.SelectedItem as GameInstance;
        ++resourceSelection;resourceList.ItemsSource=null;resourceVersions.ItemsSource=null;resourceInstallButton.IsEnabled=false;resourceStatus.Text="正在加载…";LoadState("resource","busy","正在查找"+(type switch{"mod"=>"模组","shader"=>"光影","resourcepack"=>"材质包",_=>"整合包"})+"…");
        try{using var service=new ResourceService(settings);string sort=resourceSort.SelectedIndex switch{1=>"updated",2=>"relevance",_=>"downloads"};var result=await service.SearchAsync(query,type,target,offset,cts.Token,sort);if(cts.IsCancellationRequested||resourceType!=type||downloadView!=type)return;resourceOffset=offset;totalResources=result.Total;resourceList.ItemsSource=result.Items;resourceStatus.Text=$"第 {offset/20+1} 页 · {result.Total:N0} 项 · Modrinth";LoadState("resource",result.Items.Count==0?"empty":"none","没有找到适合当前版本的内容，请换个搜索词或实例。");}
        catch(OperationCanceledException){}catch(Exception e){if(!cts.IsCancellationRequested){resourceStatus.Text="加载未完成";LoadState("resource","error","加载失败："+e.Message);}}finally{if(resourceCts==cts)resourceCts=null;cts.Dispose();}
    }
    int resourceSelection;
    async Task UpdateResourceVersionsAsync()
    {
        int generation=++resourceSelection;resourceInstallButton.IsEnabled=false;resourceVersions.ItemsSource=null;if(resourceList.SelectedItem is not ResourceProject project){resourceVersionHint.Text="选择资源后，自动匹配兼容版本。";return;}resourceVersionHint.Text="正在匹配兼容版本…";var target=targetBox.SelectedItem as GameInstance;
        try{using var service=new ResourceService(settings);var choices=await service.VersionsAsync(project.Id,project.Type,target,default);if(generation!=resourceSelection||!ReferenceEquals(resourceList.SelectedItem,project))return;resourceVersions.ItemsSource=choices;resourceVersions.SelectedItem=choices.FirstOrDefault(x=>x.Type=="release")??choices.FirstOrDefault();resourceInstallButton.IsEnabled=choices.Count>0;resourceInstallButton.Content=project.Type=="modpack"?"安装这个整合包":"一键安装到所选游戏";resourceStatus.Text=choices.Count==0?"没有适合当前游戏的发布版本，请选择其他游戏。":"已自动选择兼容稳定版本 · 双击项目查看介绍";}catch(Exception e){if(generation==resourceSelection)resourceStatus.Text=e.Message;}
    }
    async Task ShowResourceDetailsAsync()
    {
        if(resourceList.SelectedItem is not ResourceProject project)return;var target=project.Type=="modpack"?null:targetBox.SelectedItem as GameInstance;
        try{using var service=new ResourceService(settings);var versions=await service.VersionsAsync(project.Id,project.Type,target,default);var dialog=new ResourceDetailWindow(project,versions,target?.Name??""){Owner=this};if(dialog.ShowDialog()==true&&dialog.ChosenVersion is ResourceVersion chosen){resourceList.SelectedItem=project;resourceVersions.ItemsSource=versions;resourceVersions.SelectedItem=chosen;await InstallResourceAsync();}}catch(Exception e){Error(e);}
    }
    static string CleanName(string name){foreach(char c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');return name.Trim().TrimEnd('.');}
    async Task InstallResourceAsync()
    {
        if(resourceList.SelectedItem is not ResourceProject project||resourceVersions.SelectedItem is not ResourceVersion version)return;if(project.Type=="modpack"&&!EnsureSource("minecraft"))return;var target=targetBox.SelectedItem as GameInstance;if(project.Type!="modpack"&&target is null){MessageBox.Show(this,"请先选择要安装到的游戏。");return;}
        await RunAsync("安装 "+project.Name,async(p,ct)=>{using var service=new ResourceService(settings){Log=Log};if(project.Type=="modpack"){var game=await service.InstallPackAsync(version,CleanName(project.Name),p,ct);settings.LastGame=game.Id;}else{int count=await service.InstallAsync(project,version,target!,p,ct);Log($"已安装 {count} 个文件（包括必需前置）。");}},scope:target==null?null:"content:"+target.GameDirectory,destination:target?.GameDirectory??Path.Combine(settings.JavaRoot,"versions"),project:project);
    }
    async Task ImportPackAsync()
    {
        if(!EnsureSource("minecraft"))return;var dialog=new OpenFileDialog{Title="导入 Modrinth 整合包",Filter="Modrinth 整合包|*.mrpack"};if(dialog.ShowDialog()!=true)return;var file=dialog.FileName;
        await RunAsync("导入整合包",async(p,ct)=>{using var service=new ResourceService(settings){Log=Log};var game=await service.ImportPackAsync(file,"",p,ct);settings.LastGame=game.Id;});
    }
    static string AccountKey(object? a)=>a switch{MicrosoftAccount ms=>ms.Uuid,_=>""};
    void ReloadAccounts()
    {
        refreshing=true;try{var list=new List<object>();list.AddRange(accounts);if(list.Count==0)list.Add("尚未选择账号");accountBox.ItemsSource=list;accountBox.SelectedItem=list.FirstOrDefault(x=>AccountKey(x)==settings.SelectedServer)??list[0];}finally{refreshing=false;}UpdateOverview();_=RefreshSkinAsync();
    }
    void AddMicrosoftAccount()
    {
        if(active!=null)return;var dialog=new MicrosoftLoginWindow{Owner=this};if(dialog.ShowDialog()!=true||dialog.Account is not MicrosoftAccount account)return;
        try{accounts.RemoveAll(x=>x.Uuid==account.Uuid);accounts.Add(account);MicrosoftAccounts.Save(accounts);settings.SelectedServer=AccountKey(account);settings.Save();ReloadAccounts();}catch(Exception e){Error(e);}
    }
    void RemoveAccount()
    {
        if(active!=null)return;
        if(accountBox.SelectedItem is not MicrosoftAccount account)return;if(!Confirm("从本机移除 "+account.Name+" 的登录记录？"))return;try{accounts.Remove(account);MicrosoftAccounts.Save(accounts);settings.SelectedServer="";settings.Save();ReloadAccounts();}catch(Exception e){Error(e);}
    }
    async Task RefreshSkinAsync(bool reloadProfile=false)
    {
        skinCts?.Cancel();selectedSkin=displayedSkin=null;selectedSkinOwner="";applySkinButton.IsEnabled=false;skinPreview.SetSkin(null);defaultSkin.Visibility=Visibility.Visible;defaultSkin.Text="未登录";UpdateOverview();
        if(accountBox.SelectedItem is not MicrosoftAccount account){skinStatus.Text="请先登录微软账号。";return;}defaultSkin.Text="默认皮肤";
        var cts=skinCts=new();skinStatus.Text="正在读取皮肤…";
        try
        {
            string cache=SkinCachePath(account.Uuid);if(File.Exists(cache)&&new FileInfo(cache).Length<=4*1024*1024)try{ShowPlayerSkin(account,await File.ReadAllBytesAsync(cache,cts.Token));}catch(IOException){}
            if(reloadProfile||account.SkinUrl.Length>0){reloadProfile=true;using var service=new MicrosoftAccounts();account=await service.ProfileAsync(account,cts.Token);}
            if(cts.IsCancellationRequested||accountBox.SelectedItem is not MicrosoftAccount selected||selected.Uuid!=account.Uuid)return;
            if(account.SkinUrl.Length>0){var bytes=await DownloadSkinAsync(account.SkinUrl,cts.Token);if(cts.IsCancellationRequested||accountBox.SelectedItem is not MicrosoftAccount current||current.Uuid!=account.Uuid)return;ShowPlayerSkin(account,bytes);Directory.CreateDirectory(Path.GetDirectoryName(cache)!);await File.WriteAllBytesAsync(cache,bytes,cts.Token);}
            if(reloadProfile){accounts.RemoveAll(x=>x.Uuid==account.Uuid);accounts.Add(account);MicrosoftAccounts.Save(accounts);refreshing=true;try{var list=new List<object>();list.AddRange(accounts);accountBox.ItemsSource=list;accountBox.SelectedItem=account;}finally{refreshing=false;}}
            skinStatus.Text=account.Name+" · 微软正版账号"+(account.SkinUrl.Length==0?"\n角色暂未返回自定义皮肤。":"");UpdateOverview();
        }
        catch(OperationCanceledException){}catch(Exception e){if(!cts.IsCancellationRequested)skinStatus.Text="皮肤刷新未完成："+SkinAccounts.Redact(e.Message);}finally{if(skinCts==cts)skinCts=null;cts.Dispose();}
    }
    async Task PlayAsync(GameInstance? game)
    {
        if(launchPreparing||FocusLaunchedGame())return;if(game is null){MessageBox.Show(this,"请先选择一个 Java 游戏版本。");return;}if(accountBox.SelectedItem is not MicrosoftAccount account){Navigate("account");return;}settings.LastGame=game.Id;
        await RunLaunchAsync(game.Name,"java:"+settings.JavaRoot,async(p,ct)=>{p.Report(new("验证微软登录与 Java 版权限…"));using var auth=new MicrosoftAccounts();account=await auth.RefreshAsync(account,ct);accounts.RemoveAll(x=>x.Uuid==account.Uuid);accounts.Add(account);MicrosoftAccounts.Save(accounts);using var launch=new LaunchCore(settings){Log=Log};return await launch.StartMicrosoftAsync(game,account,p,ct);},time=>settings.LastPlayed[game.Id]=time);
    }    void ManageContent(){if(gameList.SelectedItem is GameInstance game)new ContentManagerWindow(game){Owner=this}.ShowDialog();}
    void OpenGameFolder(){if(gameList.SelectedItem is GameInstance game)Open(game.GameDirectory);}
    async Task RepairGameAsync()
    {
        if(gameList.SelectedItem is not GameInstance game)return;
        await RunAsync("修复 "+game.Name,async(p,ct)=>{string pack=Path.Combine(game.Folder,".blockdock-pack.json");if(game.Pending&&File.Exists(pack)){var job=JsonNode.Parse(await File.ReadAllTextAsync(pack,ct))!;using var resources=new ResourceService(settings){Log=Log};await resources.ImportPackAsync(job["package"]!.ToString(),job["name"]!.ToString(),p,ct);}else{using var launch=new LaunchCore(settings){Log=Log};launch.RepairInstanceProfile(game);await launch.CreateLauncher(game.GameDirectory,true).InstallAsync(game.Id,cancellationToken:ct);File.Delete(Path.Combine(game.Folder,".blockdock-pending"));}});
    }
    async Task BackupGameAsync()
    {
        if(gameList.SelectedItem is not GameInstance game)return;string saves=Path.Combine(game.GameDirectory,"saves");if(!Directory.Exists(saves)){MessageBox.Show(this,"这个游戏还没有存档。");return;}
        await RunAsync("备份 "+game.Name+" 的存档",async(p,ct)=>{var files=Directory.GetFiles(saves,"*",SearchOption.AllDirectories);string target=Paths.Safe(Settings.DataPath,"backups/"+CleanName(game.Name)+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(target)!);await Task.Run(()=>{using(var zip=ZipFile.Open(target+".part",ZipArchiveMode.Create))for(int i=0;i<files.Length;i++){ct.ThrowIfCancellationRequested();zip.CreateEntryFromFile(files[i],Path.GetRelativePath(saves,files[i]),CompressionLevel.Fastest);p.Report(new("备份存档…",i+1,files.Length));}File.Move(target+".part",target);},ct);Log("备份已保存："+target);});
    }
    void ArchiveGame()
    {
        if(active!=null||gameList.SelectedItem is not GameInstance game)return;try{if(Process.GetProcessesByName("java").Length+Process.GetProcessesByName("javaw").Length>0)throw new IOException("请先退出正在运行的 Java 游戏，再归档。");if(!Confirm("把“"+game.Name+"”移到 .blockdock-archive 文件夹？游戏文件会保留，可手动移回 versions 恢复。"))return;if((File.GetAttributes(game.Folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("链接目录请在资源管理器中管理。");string target=Paths.Safe(settings.JavaRoot,".blockdock-archive/"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"/"+Paths.Id(game.Id));Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(game.Folder,target);RefreshGames();}catch(Exception e){Error(e);}
    }
    void ShowBedrock(bool online){bedrockOnline=online;bedrockList.Visibility=online?Visibility.Visible:Visibility.Collapsed;packageList.Visibility=online?Visibility.Collapsed:Visibility.Visible;foreach(FrameworkElement child in bedrockButtons.Children)child.Visibility=(string)child.Tag==(online?"online":"local")?Visibility.Visible:Visibility.Collapsed;bedrockStatus.Text=online?"选择在线版本，再点击下载。":"选择独立实例，再安装 / 切换或开始游戏。";RefreshBedrockPackages();}
    void RefreshBedrockPackages(){if(!ready)return;var selected=(packageList.SelectedItem as BedrockPackage)?.Path;var list=BedrockNative.Packages().Where(x=>x.Name.Contains(bedrockSearch.Text,StringComparison.OrdinalIgnoreCase)).ToList();packageList.ItemsSource=list;packageList.SelectedItem=list.FirstOrDefault(x=>x.Path==selected)??list.FirstOrDefault();RefreshHomeBedrock();}
    void RefreshHomeBedrock(){if(!ready)return;var selected=(homeBedrock.SelectedItem as BedrockPackage)?.Path;var list=BedrockNative.Packages();homeBedrock.ItemsSource=list;homeBedrock.SelectedItem=list.FirstOrDefault(x=>x.Path==selected)??list.FirstOrDefault();RefreshLaunchButton();}
    async Task PlayHomeBedrockAsync(){if(homeBedrock.SelectedItem is not BedrockPackage package){Navigate("bedrock-local");return;}await StartBedrockSelectionAsync(package);}
    void FilterBedrock(){bedrockList.ItemsSource=bedrock.Where(x=>x.Id.Contains(bedrockSearch.Text,StringComparison.OrdinalIgnoreCase)&&(bedrockType.SelectedIndex<=0||x.Channel==(bedrockType.SelectedIndex==1?0:bedrockType.SelectedIndex==2?2:1))).ToList();}
    CancellationTokenSource? bedrockCts;
    async Task RefreshBedrockAsync()
    {
        if(!EnsureSource("bedrock"))return;bedrockCts?.Cancel();var cts=bedrockCts=new();bedrockList.ItemsSource=null;LoadState("bedrock","busy","正在读取基岩版版本…");
        try{using var dl=new Downloader(settings.Source){Log=Log};var result=await new BedrockCatalog(settings,dl).VersionsAsync(cts.Token);if(cts.IsCancellationRequested)return;bedrock=result;FilterBedrock();bedrockStatus.Text=$"找到 {bedrock.Count} 个版本";LoadState("bedrock",bedrock.Count==0?"empty":"none","这个下载源还没有可用版本。");}catch(OperationCanceledException){}catch(Exception e){if(!cts.IsCancellationRequested)LoadState("bedrock","error","版本加载失败："+e.Message);}finally{if(bedrockCts==cts)bedrockCts=null;cts.Dispose();}
    }
    async Task DownloadBedrockAsync(){if(!EnsureSource("bedrock")||bedrockList.SelectedItem is not BedrockVersion v)return;await RunAsync("下载基岩版 "+v.Id,async(p,ct)=>{using var dl=new Downloader(settings.Source){Log=Log};string file=await new BedrockCatalog(settings,dl).DownloadAsync(v,p,ct);Log("下载完成："+Path.GetFileName(file));},"bedrock-downloads");}
    async Task ImportBedrockAsync(){var d=new OpenFileDialog{Title="导入基岩版安装包",Filter="基岩版安装包|*.appx;*.msix;*.msixvc"};if(d.ShowDialog()!=true)return;await RunAsync("导入基岩版",async(p,ct)=>{await BedrockService.ImportAsync(d.FileName,ct);},"bedrock-downloads");}
    async Task InstallBedrockAsync()
    {
        if(active!=null||packageList.SelectedItem is not BedrockPackage package)return;
        try{var current=await BedrockNative.CurrentForAsync(package,default);bool replace=current!=null;if(replace&&!Confirm("当前系统安装："+current!.Version+"\n准备切换到：基岩版 "+package.Version+"\n\n每个版本使用独立数据目录，新实例不会复制原来的世界。此操作会先备份检测到的世界，再切换 Windows 注册；同一频道一次只能启用一个版本。\n\n原安装包和独立实例数据都会保留。继续切换？"))return;await RunAsync("安装 / 切换基岩版 "+package.Version,async(p,ct)=>{if(jobContext.Value is LauncherJob systemJob)systemJob.CanInterrupt=false;await BedrockNative.InstallAsync(package,replace,p,ct);Log("Windows 安装完成，现在可以开始游戏。");},"bedrock-system");}catch(Exception e){Error(e);}
    }
    async Task PlayBedrockAsync(){if(packageList.SelectedItem is BedrockPackage package){editionBox.SelectedIndex=1;homeBedrock.SelectedItem=homeBedrock.Items.Cast<BedrockPackage>().FirstOrDefault(x=>x.Path==package.Path);await StartBedrockSelectionAsync(package);}}
    void OpenBedrockFolder(bool home=false){try{var package=(home?homeBedrock.SelectedItem:packageList.SelectedItem) as BedrockPackage;if(package==null){Directory.CreateDirectory(BedrockInstances.Root);Open(BedrockInstances.Root);return;}string folder=BedrockInstances.For(package).Folder;BedrockDataLink.NoLinks(folder);Directory.CreateDirectory(folder);Open(folder);}catch(Exception e){Error(e);}}
    void OpenBackupFolder(){string path=Path.Combine(Settings.DataPath,"backups");Directory.CreateDirectory(path);Open(path);}
    void ImportProvider()
    {
        if(active!=null)return;var dialog=new OpenFileDialog{Title="导入下载源配置",Filter="下载源配置|*.json"};if(dialog.ShowDialog()!=true)return;
        try{var config=DownloadProvider.Parse(File.ReadAllText(dialog.FileName));DownloadProvider.Save(config);sourceName.Text=config.Name;javaManifestUrl.Text=config.JavaManifestUrl;bedrockUwpUrl.Text=config.BedrockUwpUrl;bedrockGdkUrl.Text=config.BedrockGdkUrl;settings.Source="Custom";settings.Save();versions.Clear();bedrock.Clear();providerHint.Text="已导入："+config.Name;UpdateOverview();}catch(Exception e){providerHint.Text=e.Message;}
    }
    void RemoveProvider(){if(active!=null)return;try{File.Delete(DownloadProvider.ConfigPath);sourceName.Clear();javaManifestUrl.Clear();bedrockUwpUrl.Clear();bedrockGdkUrl.Clear();settings.Source="Unconfigured";settings.Save();versions.Clear();bedrock.Clear();minecraftList.ItemsSource=null;bedrockList.ItemsSource=null;providerHint.Text="尚未配置游戏本体下载源。";UpdateOverview();}catch(Exception e){providerHint.Text=e.Message;}}
    void SaveSettings()
    {
        if(active!=null)return;try{string root=Path.GetFullPath(rootBox.Text.Trim());string java=javaMode.SelectedIndex==0?"":javaBox.Text.Trim();if(java.Length>0&&!File.Exists(java))throw new IOException("找不到填写的 Java 程序。");Directory.CreateDirectory(root);settings.JavaRoot=root;settings.JavaPath=java;SaveMemoryConfiguration();settings.Save();RefreshGames();_=DetectJavaAsync();NotifyStatus("设置已保存。");}catch(Exception e){Error(e);}
    }
    void Log(string text)
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>Log(text));return;}text=SkinAccounts.Redact(text);if(logBox.Text.Length>120000)logBox.Text=logBox.Text[^80000..];logBox.AppendText(DateTime.Now.ToString("HH:mm:ss ")+text+Environment.NewLine);logBox.ScrollToEnd();
        try{Directory.CreateDirectory(Settings.DataPath);File.AppendAllText(Path.Combine(Settings.DataPath,"operations.log"),DateTimeOffset.Now.ToString("O")+" "+text+Environment.NewLine);}catch(IOException){}
    }
    void ShowJob(LauncherJob job){taskTitle.Text=job.Name;taskDetail.Text=job.Metadata+"\n"+job.State+" · "+job.FileSummary+"\n\n"+job.Detail+"\n\n保存位置\n"+job.Destination+(job.Description.Length>0?"\n\n"+job.Description:"");transferList.ItemsSource=job.Files;progress.IsIndeterminate=job.Indeterminate;progress.Value=job.Percent;pauseButton.IsEnabled=job.CanCancel;}
    async Task RunAsync(string name,Func<IProgress<WorkProgress>,CancellationToken,Task> action,string? scope=null,string? destination=null,ResourceProject? project=null)
    {
        var job=new LauncherJob(name);ConfigureJob(job,project);scope??="java:"+settings.JavaRoot;job.Destination=destination??(scope.StartsWith("bedrock")?BedrockService.ArchiveRoot:settings.JavaRoot);jobs.Insert(0,job);jobList.SelectedItem=job;active??=job.Cancellation;UpdateTaskBadge();if(!jobScopes.TryGetValue(scope,out var scopeGate))jobScopes[scope]=scopeGate=new(1);Log("已添加任务："+name);long last=0;bool scopeHeld=false,slotHeld=false;
        var reporter=new Progress<WorkProgress>(x=>{if(job.Finished)return;if(Environment.TickCount64-last<120&&!(x.Total>0&&x.Done==x.Total))return;last=Environment.TickCount64;job.Detail=x.Message+(x.Total>0?$" · {x.Done}/{x.Total}":x.Size>0?$" · {x.Bytes/1048576.0:N1}/{x.Size/1048576.0:N1} MB":"");job.Indeterminate=x.Total<=0&&x.Size<=0;if(x.Size>0)job.Percent=100.0*x.Bytes/x.Size;else if(x.Total>0)job.Percent=100.0*x.Done/x.Total;if(ReferenceEquals(jobList.SelectedItem,job))ShowJob(job);});
        try{job.Detail="等待同类任务完成，可先添加其他任务。";await scopeGate.WaitAsync(job.Cancellation.Token);scopeHeld=true;await jobSlots.WaitAsync(job.Cancellation.Token);slotHeld=true;job.State="进行中";jobContext.Value=job;DownloadTelemetry.Current.Value=update=>Dispatcher.BeginInvoke(()=>{job.Transfer(job.Finished&&update.State is "下载中" or "正在连接"?update with{Speed=0,State=job.State}:update);});await action(reporter,job.Cancellation.Token);settings.Save();job.State="已完成";job.Detail="操作完成。";job.Percent=100;Log("任务完成："+name);}
        catch(OperationCanceledException){job.State="已取消";job.Detail="已停止本次任务，保留已完成文件和下载断点。";Log("任务已取消："+name);}
        catch(Exception e){job.State="未完成";job.Detail=SkinAccounts.Redact(e.Message);Log(name+"："+e);}
        finally{DownloadTelemetry.Current.Value=null;jobContext.Value=null;job.Finished=true;job.StopTransfers(job.State);UpdateTaskBadge();job.Indeterminate=false;if(slotHeld)jobSlots.Release();if(scopeHeld)scopeGate.Release();active=jobs.FirstOrDefault(x=>!x.Finished)?.Cancellation;if(ReferenceEquals(jobList.SelectedItem,job))ShowJob(job);status.Text=$"{jobs.Count(x=>!x.Finished)} 个任务进行中 / 排队 · {jobs.Count(x=>x.Finished)} 个已结束";RefreshGames();RefreshBedrockPackages();installGameButton.IsEnabled=minecraftList.SelectedItem!=null&&loaderVersion.Items.Count>0;resourceInstallButton.IsEnabled=resourceVersions.Items.Count>0;}
    }
    void Window_Closing(object? sender,CancelEventArgs e)
    {
        if(launchPreparing){e.Cancel=true;if(launchCanCancel){launchCts?.Cancel();launchStatus.Text="正在取消启动准备，请稍候…";}else launchStatus.Text="Windows 正在切换游戏，请完成后关闭。";return;}
        if(jobs.Any(x=>!x.Finished)){e.Cancel=true;foreach(var job in jobs.Where(x=>!x.Finished&&x.CanCancel))job.Cancel();NotifyStatus(jobs.Any(x=>!x.Finished&&!x.CanInterrupt)?"Windows 正在安装，请等待完成后关闭。":"正在取消任务，结束后可关闭。",true);return;}
        bedrockCts?.Cancel();listCts?.Cancel();resourceCts?.Cancel();skinCts?.Cancel();loaderCts?.Cancel();try{settings.Save();}catch{}
    }
}
