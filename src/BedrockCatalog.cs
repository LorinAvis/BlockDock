using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BlockDock;

public sealed record BedrockVersion(string Id,int Channel,string Architecture,string PackageType,string UpdateId,string[] Urls,string Identity="")
{
    public string Kind=>Channel switch {1=>"Beta",2=>"Preview",_=>"正式版"};
    public string Platform=>PackageType+" · "+Architecture;
}

// The unmodified upstream parser validates each GDK version against the exact
// Microsoft package identity. Store URL resolution uses its original public API.
public sealed class BedrockCatalog
{
    private readonly Settings settings;
    private readonly Downloader dl;
    private readonly Assembly backend;
    private readonly string engineRoot;
    private static readonly Dictionary<string,Assembly> loaded=new(StringComparer.OrdinalIgnoreCase);
    public BedrockCatalog(Settings settings,Downloader dl,string? engineDirectory=null)
    {
        this.settings=settings;this.dl=dl;
        string directory=engineDirectory??Path.GetDirectoryName(BedrockService.FindEngine(settings))??throw new IOException("基岩版组件不完整。");
        engineRoot=directory;
        string file=Path.Combine(directory,"BedrockLauncher.UpdateProcessor.dll");
        lock(loaded)
        {
            if(!loaded.TryGetValue(file,out backend!))
            {
                var context=new AssemblyLoadContext("BlockDock-Bedrock-"+loaded.Count,false);
                context.Resolving+=(ctx,name)=>{var dependency=Path.Combine(directory,name.Name+".dll");return File.Exists(dependency)?ctx.LoadFromAssemblyPath(dependency):null;};
                backend=context.LoadFromAssemblyPath(file);loaded[file]=backend;
            }
        }
    }
    private async Task<string> CachedTextAsync(string url,string cache,CancellationToken ct)
    {
        try{var text=await dl.TextAsync(url,ct);JsonNode.Parse(text);Settings.AtomicText(Path.Combine(Settings.DataPath,cache),text);return text;}
        catch(Exception)when(!ct.IsCancellationRequested&&File.Exists(Path.Combine(Settings.DataPath,cache))){dl.Log?.Invoke("联网失败，使用基岩版缓存列表："+cache);return await File.ReadAllTextAsync(Path.Combine(Settings.DataPath,cache),ct);}
    }
    public List<BedrockVersion> Parse(string uwp,string gdk)
    {
        var result=new List<BedrockVersion>();
        foreach(var row in JsonNode.Parse(uwp)!.AsArray())
        {
            if(row is not JsonArray a||a.Count<3)continue;
            string version=a[0]?.ToString()??"",uuid=a[1]?.ToString()??"";int.TryParse(a[2]?.ToString(),out var channel);
            string arch=a.Count>3?a[3]?.ToString()??"x64":"x64";
            if(!Guid.TryParse(uuid,out _)||!Version.TryParse(version,out _)||arch=="arm64")continue;
            result.Add(new(version,channel,arch,"UWP",uuid,Array.Empty<string>()));
        }
        var type=backend.GetType("BedrockLauncher.UpdateProcessor.Databases.GdkLinksDb",true)!;
        var parser=Activator.CreateInstance(type)!;type.GetMethod("Parse")!.Invoke(parser,new object[]{gdk});
        foreach(var item in (IEnumerable)type.GetProperty("Versions")!.GetValue(parser)!)
        {
            var t=item.GetType();string Field(string name)=>t.GetField(name)!.GetValue(item)?.ToString()??"";
            string arch=Field("architecture");if(arch!="x64")continue;
            result.Add(new(Field("version"),Convert.ToInt32(t.GetField("type")!.GetValue(item)),arch,"GDK",Field("uuid"),(string[])t.GetField("downloadUrls")!.GetValue(item)!,Field("packageIdentity")));
        }
        return result.OrderByDescending(x=>Version.Parse(x.Id)).ThenBy(x=>x.Channel).ThenBy(x=>x.Architecture).DistinctBy(x=>(x.Id,x.Channel,x.Architecture,x.PackageType)).ToList();
    }
    public async Task<List<BedrockVersion>> VersionsAsync(CancellationToken ct)
    {
        var source=DownloadProvider.Load();if(source is null||!DownloadProvider.HasBedrock)throw new IOException("请先到设置 → 下载源，添加自己的基岩版版本清单。本启动器不预置游戏本体下载源。");
        var uwp=source.BedrockUwpUrl.Length>0?CachedTextAsync(source.BedrockUwpUrl,"bedrock-uwp.json",ct):Task.FromResult("[]");
        var gdk=source.BedrockGdkUrl.Length>0?CachedTextAsync(source.BedrockGdkUrl,"bedrock-gdk.json",ct):Task.FromResult("[]");
        await Task.WhenAll(uwp,gdk);return Parse(await uwp,await gdk);
    }
    public async Task<string[]> ResolveAsync(BedrockVersion version,CancellationToken ct)
    {
        if(version.PackageType=="GDK")return version.Urls.SelectMany(UrlCandidates).Distinct().ToArray();
        if(version.Channel==1)throw new IOException("旧版 Beta 需要微软测试资格，本启动器暂不提供其下载。可以导入你已取得的安装包。");
        var storeType=backend.GetType("BedrockLauncher.UpdateProcessor.Handlers.StoreNetwork",true)!;
        var enumType=backend.GetType("BedrockLauncher.UpdateProcessor.Enums.VersionType",true)!;
        var store=Activator.CreateInstance(storeType)!;
        var task=(Task<string>)storeType.GetMethod("getDownloadLink")!.Invoke(store,new object[]{version.UpdateId,1,Enum.ToObject(enumType,version.Channel)})!;
        var link=await task.WaitAsync(TimeSpan.FromSeconds(60),ct);
        if(string.IsNullOrWhiteSpace(link))throw new IOException("微软已不再提供这个历史版本的安装包，可尝试其他版本或导入已有安装包。");
        return UrlCandidates(link).ToArray();
    }
    public static string OfficialUrl(string url)
    {
        var uri=new Uri(url);string host=uri.Host.ToLowerInvariant();
        if(!(host.EndsWith(".xboxlive.com",StringComparison.Ordinal)||host.EndsWith(".xboxlive.cn",StringComparison.Ordinal)||host.EndsWith(".delivery.mp.microsoft.com",StringComparison.Ordinal)))throw new IOException("安装包地址不是微软官方下载节点。");
        if(uri.Scheme is not ("http" or "https"))throw new IOException("安装包使用不受支持的协议。");
        return uri.AbsoluteUri;
    }
    private static IEnumerable<string> UrlCandidates(string original)
    {
        string url=OfficialUrl(original);var uri=new Uri(url);
        // Some Xbox delivery nodes are published with HTTP-only URLs. Try TLS
        // first, then retain the publisher's actual URL instead of inventing a node.
        if(uri.Scheme=="http")yield return new UriBuilder(uri){Scheme="https",Port=-1}.Uri.AbsoluteUri;
        yield return url;
    }
    public string Destination(BedrockVersion version)=>Paths.Safe(BedrockService.ArchiveRoot,version.PackageType=="GDK"?Paths.Id(version.Identity)+".msixvc":$"Minecraft_{Paths.Id(version.Id)}_{version.Kind}_{Paths.Id(version.Architecture)}.appx");
    public async Task<string> StageForManagerAsync(BedrockVersion version,string package,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        var engine=engineRoot;
        string config=Path.Combine(engine,"data","settings.json");
        if(!File.Exists(config))Settings.AtomicText(config,"{\"PortableMode\":true,\"KeepAppx\":true}");
        var json=JsonNode.Parse(await File.ReadAllTextAsync(config,ct))!;
        var managerRoot=json["PortableMode"]?.GetValue<bool>()==true?Path.Combine(engine,"data"):string.IsNullOrWhiteSpace(json["FixedDirectory"]?.ToString())?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),".minecraft_bedrock"):json["FixedDirectory"]!.ToString();
        string versionName=Version.Parse(version.Id).ToString();
        string relative=version.PackageType=="GDK"?$"versions/{Paths.Id(versionName)}/{Paths.Id(version.Identity)}.msixvc":$"versions/AppxBackups/Minecraft-{Paths.Id(versionName)}.Appx";
        string target=Paths.Safe(managerRoot,relative);
        long size=new FileInfo(package).Length;
        var receipt=JsonNode.Parse(await File.ReadAllTextAsync(package+".receipt.json",ct))!;
        string hash=receipt["sha1"]!.ToString();
        if(!await Downloader.ValidAsync(package,hash,size,ct))throw new IOException("下载文件与记录的校验值不一致，请重新下载后添加缓存。");
        if(await Downloader.ValidAsync(target,hash,size,ct))return target;
        if(File.Exists(target))throw new IOException("管理器缓存中已有不同内容的同版本文件，已保留原文件。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using(var input=File.OpenRead(package))
        await using(var output=new FileStream(target+".part",FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
        {
            var buffer=new byte[131072];long done=0;int read;long last=0;
            while((read=await input.ReadAsync(buffer,ct))>0){await output.WriteAsync(buffer.AsMemory(0,read),ct);done+=read;if(Environment.TickCount64-last>200){progress.Report(new("加入版本管理器缓存…",Bytes:done,Size:size));last=Environment.TickCount64;}}
        }
        ct.ThrowIfCancellationRequested();File.Move(target+".part",target);return target;
    }
    public async Task<string> DownloadAsync(BedrockVersion version,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        progress.Report(new("解析微软官方下载地址…"));var links=await ResolveAsync(version,ct);
        string target=Destination(version);Exception? last=null;
        foreach(var url in links)
        {
            try {
                long size=await dl.SizeAsync(url,ct);
                progress.Report(new($"下载基岩版 {version.Id} · {size/1073741824.0:N2} GB"));
                string priorHash="";var receipt=target+".receipt.json";
                if(File.Exists(receipt)){try{priorHash=JsonNode.Parse(await File.ReadAllTextAsync(receipt,ct))?["sha1"]?.ToString()??"";}catch(JsonException){}}
                await dl.FileAsync(new(url,target,priorHash,size),ct,progress);
                if(version.PackageType=="UWP")
                {
                    using var zip=ZipFile.OpenRead(target);var entry=zip.GetEntry("AppxManifest.xml")??throw new IOException("下载文件不含应用清单，未通过安装包检查。");
                    using var input=entry.Open();var manifest=XDocument.Load(input);
                    var identity=manifest.Root?.Elements().FirstOrDefault(x=>x.Name.LocalName=="Identity");
                    string family=identity?.Attribute("Name")?.Value??"";
                    if(!(family.Equals("Microsoft.MinecraftUWP",StringComparison.OrdinalIgnoreCase)||family.Equals("Microsoft.MinecraftWindowsBeta",StringComparison.OrdinalIgnoreCase)))throw new IOException("安装包身份不属于 Minecraft，已停止处理。");
                }
                progress.Report(new("记录安装包校验值…"));await using var file=File.OpenRead(target);string hash=Convert.ToHexString(await SHA1.HashDataAsync(file,ct)).ToLowerInvariant();
                Settings.AtomicText(receipt,JsonSerializer.Serialize(new{version.Id,version.Channel,version.Architecture,version.PackageType,version.UpdateId,version.Identity,sha1=hash,size=new FileInfo(target).Length,source=new Uri(url).Host,downloadedAt=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));
                return target;
            }catch(Exception e)when(!ct.IsCancellationRequested){last=e;dl.Log?.Invoke("基岩版节点未完成："+e.Message);}
        }
        ct.ThrowIfCancellationRequested();throw new IOException("基岩版安装包下载未完成："+last?.Message,last);
    }
}
