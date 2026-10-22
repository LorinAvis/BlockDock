using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.FileExtractors;

namespace BlockDock;

public sealed record GameInstance(string Id,string Name,string Minecraft,string Loader,string Folder,string GameDirectory,bool Pending=false)
{
    public string Summary=>$"Minecraft {Minecraft} · {(Loader=="vanilla"?"原版":Loader)}";
    public string State=>Pending?"需要修复":"可启动";
    public string LoaderName=>Loader=="vanilla"?"原版":Loader;
    public System.Windows.Media.ImageSource Icon=>ContentIcons.Instance(this);
    public bool Favorite {get;init;}
    public DateTimeOffset? LastPlayed {get;init;}
    public string FavoriteLabel=>Favorite?"★ 已收藏":"☆ 收藏";
    public string LastPlayedLabel=>LastPlayed is DateTimeOffset time?"上次启动："+time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"):"尚无本启动器的启动记录";
    public string ContentSummary {get;init;}="";
    public override string ToString()=>Name+"  ·  "+(Loader=="vanilla"?"原版":Loader);
}
public sealed record LoaderChoice(string Name,string Version,string Hint="")
{
    public override string ToString()=>string.IsNullOrWhiteSpace(Version)?"自动选择稳定版":Version+(Hint.Length>0?" · "+Hint:"");
}
public sealed class MinecraftHttpHandler(string source):DelegatingHandler(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.All})
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var original=request.RequestUri!.AbsoluteUri;var mirror=source=="Custom"?Downloader.Mirror(original):null;
        if(request.RequestUri.AbsolutePath.Contains("version_manifest",StringComparison.Ordinal)&&request.RequestUri.Host.EndsWith("mojang.com",StringComparison.Ordinal))mirror=DownloadProvider.JavaManifest();
        if(!DownloadProvider.HasJava&&DownloadProvider.IsGameHost(request.RequestUri.Host))throw new IOException("缺少游戏文件。请在设置 → 下载源添加自己的本体下载源，再修复游戏。");
        var urls=new[]{mirror??original};
        Exception? last=null;
        foreach(var url in urls)
        {
            try{
                using var copy=new HttpRequestMessage(request.Method,url){Version=request.Version};foreach(var h in request.Headers){if(url!=original&&(h.Key.Equals("Authorization",StringComparison.OrdinalIgnoreCase)||h.Key.Equals("Cookie",StringComparison.OrdinalIgnoreCase)))continue;copy.Headers.TryAddWithoutValidation(h.Key,h.Value);}
                if(request.Content!=null)copy.Content=new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct));
                var response=await base.SendAsync(copy,ct);if(response.IsSuccessStatusCode||url==urls[^1])return response;response.Dispose();
            }catch(Exception e)when(!ct.IsCancellationRequested){last=e;}
        }
        ct.ThrowIfCancellationRequested();throw new HttpRequestException("Minecraft 下载节点暂时不可用。",last);
    }
}
public sealed class LaunchCore : IDisposable
{
    readonly Settings settings;readonly HttpClient http;
    public Action<string>? Log {get;set;}
    public LaunchCore(Settings settings){this.settings=settings;http=new HttpClient(new MinecraftHttpHandler(settings.Source)){Timeout=TimeSpan.FromMinutes(10)};http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockDock/2.0");}
    public static List<GameInstance> Scan(Settings settings)
    {
        var list=new List<GameInstance>();
        foreach(var v in JavaService.Scan(settings.JavaRoot))
        {
            try{
                var meta=ReadMetadata(settings.JavaRoot,v.Id);string baseId=meta["clientVersion"]?.ToString()??meta["minecraftVersion"]?.ToString()??v.Id;
                var json=JsonNode.Parse(File.ReadAllText(Path.Combine(v.Folder,v.Id+".json")))!;var seen=new HashSet<string>();var node=json;
                while(node["inheritsFrom"]?.ToString() is string parent&&seen.Add(parent)){baseId=parent;var file=Paths.Safe(settings.JavaRoot,$"versions/{Paths.Id(parent)}/{parent}.json");if(!File.Exists(file))break;node=JsonNode.Parse(File.ReadAllText(file))!;baseId=node["clientVersion"]?.ToString()??baseId;}
                string loader=v.Type is "Fabric" or "Forge" or "NeoForge"?v.Type.ToLowerInvariant():meta.ToJsonString().Contains("quilt-loader")?"quilt":"vanilla";
                string name=v.Id,gameDirectory=v.Folder;string profile=Path.Combine(v.Folder,".blockdock-profile.json");
                if(File.Exists(profile)){var own=JsonNode.Parse(File.ReadAllText(profile))!;name=own["name"]?.ToString()??name;baseId=own["minecraft"]?.ToString()??baseId;loader=own["loader"]?.ToString()??loader;}
                else if(!new[]{"saves","mods","config"}.Any(d=>Directory.Exists(Path.Combine(v.Folder,d)))&&!File.Exists(Path.Combine(v.Folder,"options.txt")))gameDirectory=settings.JavaRoot;
                list.Add(new(v.Id,name,baseId,loader,v.Folder,gameDirectory,v.Pending));
            }catch{list.Add(new(v.Id,v.Id,"未知版本","vanilla",v.Folder,v.Folder,true));}
        }
        return list;
    }
    public static JsonObject ReadMetadata(string root,string id)
    {
        var seen=new HashSet<string>();JsonObject merged=new();
        void Read(string current)
        {
            if(!seen.Add(current)||seen.Count>16)throw new IOException("版本继承存在循环。");
            var json=JsonNode.Parse(File.ReadAllText(Paths.Safe(root,$"versions/{Paths.Id(current)}/{current}.json")))!.AsObject();
            if(json["inheritsFrom"]?.ToString() is string parent)Read(parent);
            foreach(var p in json)merged[p.Key]=p.Value?.DeepClone();
        }
        Read(id);return merged;
    }
    public MinecraftLauncher CreateLauncher(string? gameDirectory=null,bool localOnly=false,int requiredJava=0)
    {
        var path=new MinecraftPath(gameDirectory??settings.JavaRoot){Versions=Path.Combine(settings.JavaRoot,"versions"),Library=Path.Combine(settings.JavaRoot,"libraries"),Assets=Path.Combine(settings.JavaRoot,"assets"),Runtime=Path.Combine(Settings.DataPath,"runtime")};
        var options=MinecraftLauncherParameters.CreateDefault(path,http);
        if((!string.IsNullOrWhiteSpace(settings.JavaPath)&&File.Exists(settings.JavaPath)||requiredJava>0&&JavaRuntimes.Select(JavaRuntimes.Detect(),requiredJava)!=null)&&options.FileExtractors!=null)
            foreach(var extractor in options.FileExtractors.OfType<JavaFileExtractor>().ToArray())options.FileExtractors.Remove(extractor);
        if(localOnly||!DownloadProvider.HasJava)options.VersionLoader=new LocalJsonVersionLoader(path);
        else if(options.VersionLoader is MojangJsonVersionLoaderV2 loader)loader.UseLocalManifestWhenError=true;
        return new MinecraftLauncher(options);
    }
    public async Task<List<LoaderChoice>> LoadersAsync(string minecraft,string loader,CancellationToken ct)
    {
        if(loader=="vanilla")return new(){new("vanilla","", "原版")};
        if(loader=="fabric"){
            var data=JsonNode.Parse(await http.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader/"+Uri.EscapeDataString(minecraft),ct))!.AsArray();
            return data.Select(x=>new LoaderChoice("fabric",x!["loader"]!["version"]!.ToString(),x["loader"]!["stable"]?.GetValue<bool>()==true?"稳定版":"测试版")).ToList();
        }
        if(loader=="forge"){
            var values=await new ForgeInstaller(CreateLauncher(),http).GetForgeVersions(minecraft).WaitAsync(ct);
            return values.Select(x=>new LoaderChoice(loader,x.ForgeVersionName,x.IsRecommendedVersion?"推荐":x.IsLatestVersion?"最新":"")).ToList();
        }
        if(loader=="neoforge"){
            var values=await new NeoForgeInstaller(CreateLauncher(),http).GetNeoForgeVersions(minecraft).WaitAsync(ct);
            return values.Select(x=>new LoaderChoice(loader,x.NeoForgeVersionName)).ToList();
        }
        throw new IOException("暂不支持这个模组加载器。");
    }
    public async Task<GameInstance> InstallAsync(GameVersion version,string loader,string loaderVersion,string displayName,IProgress<WorkProgress> p,CancellationToken ct)
    {
        _=DownloadProvider.JavaManifest();
        using var dl=new Downloader(settings.Source){Log=Log};var vanilla=new JavaService(settings,dl);string installedId=version.Id;
        p.Report(new("准备安装 Minecraft "+version.Id));
        if(loader=="fabric")installedId=(await vanilla.FabricAsync(version,p,ct,string.IsNullOrWhiteSpace(loaderVersion)?null:loaderVersion)).Id;
        else if(loader is "forge" or "neoforge"){
            var launcher=CreateLauncher();var file=new Progress<InstallerProgressChangedEventArgs>(x=>p.Report(new("安装 "+x.Name,x.ProgressedTasks,x.TotalTasks)));
            var bytes=new Progress<ByteProgress>(x=>p.Report(new("正在下载安装文件…",Bytes:x.ProgressedBytes,Size:x.TotalBytes)));
            var options=new ForgeInstallOptions{CancellationToken=ct,FileProgress=file,ByteProgress=bytes,InstallerOutput=new Progress<string>(x=>Log?.Invoke(x)),JavaPath=string.IsNullOrWhiteSpace(settings.JavaPath)?null:settings.JavaPath};
            if(loader=="forge"){var installer=new ForgeInstaller(launcher,http);installedId=string.IsNullOrWhiteSpace(loaderVersion)?await installer.Install(version.Id,options):await installer.Install(version.Id,loaderVersion,options);}
            else{var installer=new NeoForgeInstaller(launcher,http);installedId=string.IsNullOrWhiteSpace(loaderVersion)?await installer.Install(version.Id,options):await installer.Install(version.Id,loaderVersion,options);}
            await launcher.InstallAsync(installedId,file,bytes,ct);
        }else await vanilla.InstallAsync(version,p,ct);
        return CreateInstance(installedId,version.Id,loader,displayName);
    }
    public GameInstance CreateInstance(string installedId,string minecraft,string loader,string displayName)
    {
        string name=string.IsNullOrWhiteSpace(displayName)?minecraft+(loader=="vanilla"?" 原版":" "+loader):displayName.Trim();
        Paths.Id(name);string id=name;int counter=2;
        while(Directory.Exists(Paths.Safe(settings.JavaRoot,"versions/"+id)))id=name+" ("+(counter++)+")";
        var profile=VersionProfiles.Create(settings.JavaRoot,installedId,id);var folder=Paths.Safe(settings.JavaRoot,"versions/"+id);Directory.CreateDirectory(folder);
        Settings.AtomicText(Path.Combine(folder,id+".json"),profile.ToJsonString());
        Settings.AtomicText(Path.Combine(folder,".blockdock-profile.json"),JsonSerializer.Serialize(new{name,minecraft,loader,sourceVersion=installedId}));
        foreach(var sub in new[]{"mods","resourcepacks","shaderpacks","saves"})Directory.CreateDirectory(Path.Combine(folder,sub));
        return new(id,name,minecraft,loader,folder,folder);
    }
    public void RepairInstanceProfile(GameInstance game)
    {
        string own=Path.Combine(game.Folder,".blockdock-profile.json"),json=Path.Combine(game.Folder,game.Id+".json");if(!File.Exists(own))return;var original=JsonNode.Parse(File.ReadAllText(json))?.AsObject();if(original?["mainClass"]!=null)return;string? source=JsonNode.Parse(File.ReadAllText(own))?["sourceVersion"]?.ToString();if(source==null||source==game.Id)return;var fixedProfile=VersionProfiles.Create(settings.JavaRoot,source,game.Id);string backup=Paths.Safe(Settings.DataPath,"backups/version-profiles/"+Paths.Id(game.Id)+"-"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+".json");Settings.AtomicText(backup,original!.ToJsonString());Settings.AtomicText(json,fixedProfile.ToJsonString());Log?.Invoke("已备份并修复实例版本配置："+game.Name);
    }
    public async Task<Process> PrepareProcessAsync(GameInstance game,MicrosoftAccount account,IProgress<WorkProgress> p,CancellationToken ct,bool install=true)
    {
        if(account.AccessToken.Length<10||account.Uuid.Length!=32)throw new IOException("请先使用拥有 Java 版的微软账号登录。");
        return await PrepareSessionAsync(game,account.Session,p,ct,install);
    }
    async Task<Process> PrepareSessionAsync(GameInstance game,MSession session,IProgress<WorkProgress> p,CancellationToken ct,bool install)
    {
        if(game.Pending)throw new IOException("这个游戏尚未安装完成，请先修复文件。");
        var launcher=CreateLauncher(game.GameDirectory,true,RequiredJava(game));
        if(install){p.Report(new("准备游戏文件与 Java 运行环境…"));await launcher.InstallAsync(game.Id,new Progress<InstallerProgressChangedEventArgs>(x=>p.Report(new("检查 "+x.Name,x.ProgressedTasks,x.TotalTasks))),new Progress<ByteProgress>(x=>p.Report(new("下载启动所需文件…",Bytes:x.ProgressedBytes,Size:x.TotalBytes))),ct);}
        string? javaPath=string.IsNullOrWhiteSpace(settings.JavaPath)?JavaRuntimes.Select(JavaRuntimes.Detect(),RequiredJava(game))?.Path:settings.JavaPath;
        bool java32=javaPath!=null&&JavaRuntimes.Detect().Any(x=>x.Path.Equals(javaPath,StringComparison.OrdinalIgnoreCase)&&x.Architecture is "x86" or "i386" or "i686");int memory=MemoryAllocation.Resolve(settings,game,java32:java32);Log?.Invoke("游戏内存："+(settings.MemoryMode=="Manual"?"手动 ":"自动 ")+MemoryAllocation.Format(memory));
        var option=new MLaunchOption{Session=session,MaximumRamMb=memory,MinimumRamMb=512,GameLauncherName="BlockDock",GameLauncherVersion="2.9.0",ScreenWidth=1100,ScreenHeight=700,JavaPath=javaPath};
        p.Report(new("正在启动 "+game.Name));var process=await launcher.BuildProcessAsync(game.Id,option,ct);
        process.StartInfo.UseShellExecute=false;process.StartInfo.CreateNoWindow=true;process.StartInfo.RedirectStandardOutput=true;process.StartInfo.RedirectStandardError=true;
        return process;
    }
    public async Task<Process> StartMicrosoftAsync(GameInstance game,MicrosoftAccount account,IProgress<WorkProgress> p,CancellationToken ct)
    {
        if(account.AccessToken.Length<10||account.Uuid.Length!=32)throw new IOException("请先使用拥有 Java 版的微软账号登录。");
        var process=await PrepareProcessAsync(game,account,p,ct);ct.ThrowIfCancellationRequested();return StartProcess(process);
    }
    Process StartProcess(Process process)
    {
        void Output(object s,DataReceivedEventArgs e){if(e.Data!=null)Log?.Invoke(SkinAccounts.Redact(e.Data));}process.OutputDataReceived+=Output;process.ErrorDataReceived+=Output;process.EnableRaisingEvents=true;
        if(!process.Start())throw new IOException("Java 未能启动。");process.BeginOutputReadLine();process.BeginErrorReadLine();return process;
    }
    public int RequiredJava(GameInstance game){try{return ReadMetadata(settings.JavaRoot,game.Id)["javaVersion"]?["majorVersion"]?.GetValue<int>()??8;}catch{return 8;}}
    public void Dispose()=>http.Dispose();
}
