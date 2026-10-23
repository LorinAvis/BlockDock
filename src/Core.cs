using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlockDock;

public sealed class Settings
{
    public static string DataPath => Path.Combine(AppContext.BaseDirectory,"data");
    public string JavaRoot {get;set;} = Path.Combine(DataPath,"java",".minecraft");
    public string SelectedServer {get;set;} = "";
    public string LastGame {get;set;} = "";
    public string JavaPath {get;set;} = "";
    public int MemoryMb {get;set;} = 4096;
    public string MemoryMode {get;set;} = "Auto";
    public int ParallelTasks {get;set;} = 3;
    public string Source {get;set;} = "Unconfigured";
    public int Parallel {get;set;} = 6;
    public List<string> Favorites {get;set;} = new();
    public Dictionary<string,DateTimeOffset> LastPlayed {get;set;} = new();
    public string WallpaperFile {get;set;} = "";
    public string WallpaperEngineRoot {get;set;} = "";
    public string WallpaperFit {get;set;} = "Fill";
    public double WallpaperBrightness {get;set;} = .8;
    public double PanelOpacity {get;set;} = .72;
    public bool DownloadSourceIntroductionSeen {get;set;}
    public bool BedrockAutoSwitch {get;set;} = true;
    public bool CheckUpdatesOnStartup {get;set;} = true;
    public bool IncludePreviewUpdates {get;set;} = true;
    public string BedrockEngine => Path.Combine(AppContext.BaseDirectory,"engines","BedrockLauncher");
    public static Settings Load()
    {
        var path=Path.Combine(DataPath,"settings.json");
        if(!File.Exists(path))return new();
        try { var value=JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();value.Source=File.Exists(DownloadProvider.ConfigPath)?"Custom":"Unconfigured";value.MemoryMode=value.MemoryMode=="Manual"?"Manual":"Auto";value.MemoryMb=Math.Clamp(value.MemoryMb,512,32768);return value; }
        catch(Exception e) { throw new IOException("设置文件无法读取。请检查 data/settings.json，原文件已保留。",e); }
    }
    public void Save() { Directory.CreateDirectory(DataPath); AtomicText(Path.Combine(DataPath,"settings.json"), JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true})); }
    public static void AtomicText(string path,string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);var temp=path+"."+Guid.NewGuid().ToString("N")+".new";try{File.WriteAllText(temp,text);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);} }
}

public sealed record GameVersion(string Id,string Type,string Date,string JsonUrl="",string Sha1="",string Folder="",bool Pending=false)
{
    public string Kind => (Pending?"未完成 · ":"")+(Type switch { "release"=>"正式版", "snapshot"=>"快照版", "old_beta"=>"远古 Beta", "old_alpha"=>"远古 Alpha", _=>Type });
}
public sealed record DownloadFile(string Url,string Path,string Sha1="",long Size=0);
public sealed record WorkProgress(string Message,int Done=0,int Total=0,long Bytes=0,long Size=0);
public sealed record InstallResult(int Files,int Downloaded,long Bytes);

public static class Paths
{
    public static string Safe(string root,string relative)
    {
        var fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var full=Path.GetFullPath(Path.Combine(fullRoot,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(fullRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("版本数据包含越界路径，已停止操作。");
        return full;
    }
    public static string Id(string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id is "." or ".."||id.IndexOfAny(Path.GetInvalidFileNameChars())>=0||id.EndsWith('.')||id.EndsWith(' '))throw new IOException("版本名称包含无效字符。");
        return id;
    }
    public static void Open(string file) => Process.Start(new ProcessStartInfo(file){UseShellExecute=true});
}

public sealed class Downloader : IDisposable
{
    private readonly HttpClient http;
    private readonly string source;
    private readonly ConcurrentDictionary<string,SemaphoreSlim> locks=new(StringComparer.OrdinalIgnoreCase);
    public Action<string>? Log {get;set;}
    public Downloader(string source="Official",HttpMessageHandler? handler=null)
    {
        this.source=source;
        http=handler is null ? new HttpClient(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.None}) : new HttpClient(handler);
        http.Timeout=Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockDock/2.0");
    }
    public static string? Mirror(string original)
    {
        return DownloadProvider.Rewrite(original);
    }
    public IEnumerable<string> Candidates(string url)
    {
        if(source=="Unconfigured"&&Uri.TryCreate(url,UriKind.Absolute,out var gameUri)&&DownloadProvider.IsGameHost(gameUri.Host))throw new IOException("请先在设置 → 下载源添加自己的游戏本体下载源。");
        var custom=source=="Custom"?Mirror(url):null;
        yield return custom??url;
    }
    public async Task<string> TextAsync(string url,CancellationToken ct)
    {
        Exception? last=null;
        foreach(var candidate in Candidates(url))
        {
            try {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using var response=await http.GetAsync(candidate,timeout.Token);response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token);
            } catch(Exception e) when(!ct.IsCancellationRequested) { last=e;Log?.Invoke("版本源暂不可用："+new Uri(candidate).Host); }
        }
        ct.ThrowIfCancellationRequested();throw new IOException("读取版本列表失败："+last?.Message,last);
    }
    public async Task<long> SizeAsync(string url,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var req=new HttpRequestMessage(HttpMethod.Get,url);req.Headers.Range=new RangeHeaderValue(0,0);
        using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
        long size=response.Content.Headers.ContentRange?.Length??response.Content.Headers.ContentLength??0;
        if(size<=0)throw new IOException("下载服务器未提供有效文件长度，无法验证完整性。");return size;
    }
    public static async Task<bool> ValidAsync(string path,string hash,long size,CancellationToken ct)
    {
        if(!File.Exists(path))return false;
        if(size>0&&new FileInfo(path).Length!=size)return false;
        if(string.IsNullOrEmpty(hash))return size>0?true:new FileInfo(path).Length>0;
        await using var file=File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(file,ct)).Equals(hash,StringComparison.OrdinalIgnoreCase);
    }
    public async Task<bool> FileAsync(DownloadFile file,CancellationToken ct,IProgress<WorkProgress>? progress=null)
    {
        var gate=locks.GetOrAdd(Path.GetFullPath(file.Path),_=>new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try{DownloadTelemetry.Report(Path.GetFileName(file.Path),file.Path,0,file.Size,0,"正在连接");bool downloaded=await FileCoreAsync(file,ct,progress);long length=new FileInfo(file.Path).Length;DownloadTelemetry.Report(Path.GetFileName(file.Path),file.Path,length,length,0,downloaded?"已完成":"已存在");return downloaded;}catch(OperationCanceledException){DownloadTelemetry.Report(Path.GetFileName(file.Path),file.Path,File.Exists(file.Path+".part")?new FileInfo(file.Path+".part").Length:0,file.Size,0,"已取消");throw;}catch{DownloadTelemetry.Report(Path.GetFileName(file.Path),file.Path,0,file.Size,0,"未完成");throw;}finally{gate.Release();}
    }
    private async Task<bool> FileCoreAsync(DownloadFile file,CancellationToken ct,IProgress<WorkProgress>? progress)
    {
        if(await ValidAsync(file.Path,file.Sha1,file.Size,ct))return false;
        if(!Uri.TryCreate(file.Url,UriKind.Absolute,out var original)||original.Scheme is not ("https" or "http"))throw new IOException("无效的下载地址。");
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        string temp=file.Path+".part";
        Exception? last=null;
        foreach(var url in Candidates(file.Url))for(int attempt=0;attempt<2;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try {
                if(file.Size>0&&File.Exists(temp)&&new FileInfo(temp).Length>=file.Size)
                {
                    if(await ValidAsync(temp,file.Sha1,file.Size,ct)){File.Move(temp,file.Path,true);return true;}
                    File.Delete(temp);
                }
                long offset=File.Exists(temp)?new FileInfo(temp).Length:0;
                using var req=new HttpRequestMessage(HttpMethod.Get,url);
                if(offset>0)req.Headers.Range=new RangeHeaderValue(offset,null);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(40));
                using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable&&offset>0){File.Delete(temp);throw new IOException("服务器已重置下载断点，正在重新获取。");}
                response.EnsureSuccessStatusCode();
                bool append=offset>0&&response.StatusCode==HttpStatusCode.PartialContent;
                if(append&&response.Content.Headers.ContentRange?.From!=offset)throw new IOException("服务器返回的续传位置不一致。");
                if(!append)offset=0;
                long expected=file.Size>0?file.Size:(response.Content.Headers.ContentLength??0)+offset;
                await using(var input=await response.Content.ReadAsStreamAsync(ct))
                await using(var output=new FileStream(temp,append?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
                {
                    byte[] buffer=new byte[131072];long current=offset;long lastReport=0;long sampled=offset;var timer=Stopwatch.StartNew();double sampleTime=0;
                    while(true)
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(40));
                        int read=await input.ReadAsync(buffer,timeout.Token);if(read==0)break;
                        await output.WriteAsync(buffer.AsMemory(0,read),ct);current+=read;
                        if(Environment.TickCount64-lastReport>180){double elapsed=timer.Elapsed.TotalSeconds;double speed=(current-sampled)/Math.Max(.001,elapsed-sampleTime);DownloadTelemetry.Report(Path.GetFileName(file.Path),file.Path,current,expected,speed,"下载中");sampled=current;sampleTime=elapsed;progress?.Report(new(Path.GetFileName(file.Path),Bytes:current,Size:expected));lastReport=Environment.TickCount64;}
                    }
                }
                if(!await ValidAsync(temp,file.Sha1,expected,ct)){File.Delete(temp);throw new IOException("文件长度或 SHA-1 校验失败。");}
                File.Move(temp,file.Path,true);return true;
            }catch(Exception e) when(!ct.IsCancellationRequested)
            {last=e;Log?.Invoke($"下载重试 {Path.GetFileName(file.Path)}：{e.Message}");await Task.Delay(350,ct);}
        }
        ct.ThrowIfCancellationRequested();throw new IOException($"下载未完成：{Path.GetFileName(file.Path)}\n{last?.Message}",last);
    }
    public void Dispose()=>http.Dispose();
}

public sealed class JavaService
{
    private readonly Settings settings;
    private readonly Downloader dl;
    public JavaService(Settings settings,Downloader dl){this.settings=settings;this.dl=dl;}
    public static List<GameVersion> Scan(string root)
    {
        var result=new List<GameVersion>();var versions=Path.Combine(root,"versions");
        if(!Directory.Exists(versions))return result;
        foreach(var directory in Directory.EnumerateDirectories(versions))
        {
            var id=Path.GetFileName(directory);var file=Path.Combine(directory,id+".json");
            if(!File.Exists(file))continue;
            try {
                var json=JsonNode.Parse(File.ReadAllText(file))!;
                string text=json.ToJsonString();
                string type=text.Contains("fabric-loader",StringComparison.OrdinalIgnoreCase)?"Fabric":text.Contains("neoforge",StringComparison.OrdinalIgnoreCase)?"NeoForge":text.Contains("minecraftforge",StringComparison.OrdinalIgnoreCase)||text.Contains("forge:",StringComparison.OrdinalIgnoreCase)?"Forge":json["type"]?.GetValue<string>()??"自定义";
                result.Add(new(id,type,json["releaseTime"]?.GetValue<string>()?.Split('T')[0]??"",Folder:directory,Pending:File.Exists(Path.Combine(directory,".blockdock-pending"))));
            } catch {result.Add(new(id,"配置异常","",Folder:directory));}
        }
        return result.OrderByDescending(x=>x.Date).ThenBy(x=>x.Id).ToList();
    }
    public async Task<List<GameVersion>> VersionsAsync(CancellationToken ct)
    {
        var text=await dl.TextAsync(DownloadProvider.JavaManifest(),ct);
        var json=JsonNode.Parse(text)??throw new IOException("版本列表为空。");
        var versions=json["versions"]!.AsArray().Select(v=>new GameVersion(v!["id"]!.GetValue<string>(),v["type"]!.GetValue<string>(),v["releaseTime"]!.GetValue<string>().Split('T')[0],v["url"]!.GetValue<string>(),v["sha1"]?.GetValue<string>()??"")).ToList();
        Settings.AtomicText(Path.Combine(Settings.DataPath,"java-manifest.json"),text);
        return versions;
    }
    public static List<GameVersion> CachedVersions()
    {
        try {var json=JsonNode.Parse(File.ReadAllText(Path.Combine(Settings.DataPath,"java-manifest.json")))!;return json["versions"]!.AsArray().Select(v=>new GameVersion(v!["id"]!.GetValue<string>(),v["type"]!.GetValue<string>(),v["releaseTime"]!.GetValue<string>().Split('T')[0],v["url"]!.GetValue<string>(),v["sha1"]?.GetValue<string>()??"")).ToList();}catch{return new();}
    }
    public static bool Allowed(JsonNode? rules)
    {
        if(rules is not JsonArray arr||arr.Count==0)return true;
        bool allowed=false;
        foreach(var rule in arr)
        {
            if(rule is null)continue;
            var os=rule["os"];
            if(os?["name"]?.GetValue<string>() is string name&&name!="windows")continue;
            if(os?["arch"]?.GetValue<string>() is string arch&&!(arch is "x86_64" or "amd64" or "x64"))continue;
            if(os?["version"]?.GetValue<string>() is string version&&!Regex.IsMatch(Environment.OSVersion.Version.ToString(),version))continue;
            if(rule["features"] is JsonObject features&&features.Any(x=>x.Value?.GetValue<bool>()==true))continue;
            allowed=rule["action"]?.GetValue<string>()=="allow";
        }
        return allowed;
    }
    private async Task<JsonObject> MetadataAsync(string id,GameVersion? remote,CancellationToken ct,HashSet<string> stack,List<JsonObject> chain)
    {
        Paths.Id(id);if(!stack.Add(id)||stack.Count>16)throw new IOException("版本继承链包含循环或层数过多。");
        string file=Paths.Safe(settings.JavaRoot,$"versions/{id}/{id}.json");
        if(!File.Exists(file))
        {
            if(remote is null||remote.Id!=id){var all=await VersionsAsync(ct);remote=all.FirstOrDefault(v=>v.Id==id)??throw new IOException("找不到父版本："+id);}
            await dl.FileAsync(new(remote.JsonUrl,file,remote.Sha1),ct);
        }
        var json=JsonNode.Parse(await File.ReadAllTextAsync(file,ct))?.AsObject()??throw new IOException("版本配置无法读取。");
        if(json["inheritsFrom"]?.GetValue<string>() is string parent)await MetadataAsync(parent,null,ct,stack,chain);
        chain.Add(json);return json;
    }
    public static List<DownloadFile> LibraryFiles(JsonObject json,string root)
    {
        var files=new List<DownloadFile>();
        if(json["libraries"] is not JsonArray libraries)return files;
        foreach(var lib in libraries)
        {
            if(lib is null||!Allowed(lib["rules"]))continue;
            var artifact=lib["downloads"]?["artifact"];
            if(artifact?["url"]?.GetValue<string>() is string url&&!string.IsNullOrWhiteSpace(url))
            {
                var path=artifact["path"]?.GetValue<string>()??MavenPath(lib["name"]!.GetValue<string>());
                files.Add(new(url,Paths.Safe(root,"libraries/"+path),artifact["sha1"]?.GetValue<string>()??"",artifact["size"]?.GetValue<long>()??0));
            }
            else if(lib["name"]?.GetValue<string>() is string name&&lib["natives"] is null)
            {
                string path=MavenPath(name);string baseUrl=lib["url"]?.GetValue<string>()??"https://libraries.minecraft.net/";
                files.Add(new(baseUrl.TrimEnd('/')+"/"+path,Paths.Safe(root,"libraries/"+path)));
            }
            if(lib["natives"]?["windows"]?.GetValue<string>() is string classifier)
            {
                classifier=classifier.Replace("${arch}","64");
                var native=lib["downloads"]?["classifiers"]?[classifier];
                if(native?["url"]?.GetValue<string>() is string nativeUrl)
                    files.Add(new(nativeUrl,Paths.Safe(root,"libraries/"+native["path"]!.GetValue<string>()),native["sha1"]?.GetValue<string>()??"",native["size"]?.GetValue<long>()??0));
            }
        }
        return files;
    }
    public static string MavenPath(string name)
    {
        var ext=name.Split('@');var parts=ext[0].Split(':');
        if(parts.Length<3)throw new IOException("依赖库名称无效："+name);
        return parts[0].Replace('.','/')+"/"+parts[1]+"/"+parts[2]+"/"+parts[1]+"-"+parts[2]+(parts.Length>3?"-"+parts[3]:"")+"."+(ext.Length>1?ext[1]:"jar");
    }
    public async Task<InstallResult> InstallAsync(GameVersion version,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        string marker=Paths.Safe(settings.JavaRoot,$"versions/{Paths.Id(version.Id)}/.blockdock-pending");
        Settings.AtomicText(marker,"下载 / 修复尚未完成。请在方块港中重新执行校验并修复。\n"+DateTimeOffset.Now);
        var result=await InstallCoreAsync(version,progress,ct);
        File.Delete(marker);return result;
    }
    private async Task<InstallResult> InstallCoreAsync(GameVersion version,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        progress.Report(new("读取版本与继承信息…"));
        var chain=new List<JsonObject>();await MetadataAsync(version.Id,version,ct,new(),chain);
        var jobs=new List<DownloadFile>();JsonNode? asset=null;JsonNode? logging=null;
        foreach(var json in chain)
        {
            var id=json["id"]!.GetValue<string>();Paths.Id(id);
            if(json["downloads"]?["client"] is JsonNode client)
            {
                string jarId=json["jar"]?.GetValue<string>()??id;
                jobs.Add(new(client["url"]!.GetValue<string>(),Paths.Safe(settings.JavaRoot,$"versions/{Paths.Id(jarId)}/{jarId}.jar"),client["sha1"]?.GetValue<string>()??"",client["size"]?.GetValue<long>()??0));
            }
            jobs.AddRange(LibraryFiles(json,settings.JavaRoot));
            asset=json["assetIndex"]??asset;logging=json["logging"]?["client"]?["file"]??logging;
        }
        if(logging is not null)jobs.Add(new(logging["url"]!.GetValue<string>(),Paths.Safe(settings.JavaRoot,"assets/log_configs/"+logging["id"]!.GetValue<string>()),logging["sha1"]?.GetValue<string>()??"",logging["size"]?.GetValue<long>()??0));
        if(asset is not null)
        {
            progress.Report(new("读取资源索引…"));
            var index=Paths.Safe(settings.JavaRoot,"assets/indexes/"+Paths.Id(asset["id"]!.GetValue<string>())+".json");
            await dl.FileAsync(new(asset["url"]!.GetValue<string>(),index,asset["sha1"]?.GetValue<string>()??"",asset["size"]?.GetValue<long>()??0),ct);
            var data=JsonNode.Parse(await File.ReadAllTextAsync(index,ct))!;
            foreach(var item in data["objects"]!.AsObject())
            {
                string hash=item.Value!["hash"]!.GetValue<string>();
                if(!Regex.IsMatch(hash,"^[0-9a-fA-F]{40}$"))throw new IOException("资源索引中存在无效的文件校验值。");
                jobs.Add(new("https://resources.download.minecraft.net/"+hash[..2]+"/"+hash,Paths.Safe(settings.JavaRoot,"assets/objects/"+hash[..2]+"/"+hash),hash,item.Value["size"]!.GetValue<long>()));
            }
            // Older releases require the virtual assets tree as well as content-addressed objects.
            await DownloadAllAsync(jobs,progress,ct);
            if(data["virtual"]?.GetValue<bool>()==true||data["map_to_resources"]?.GetValue<bool>()==true)
            {
                progress.Report(new("整理旧版资源目录…"));
                foreach(var item in data["objects"]!.AsObject())
                {
                    ct.ThrowIfCancellationRequested();var hash=item.Value!["hash"]!.GetValue<string>();
                    var source=Paths.Safe(settings.JavaRoot,"assets/objects/"+hash[..2]+"/"+hash);
                    if(data["virtual"]?.GetValue<bool>()==true)CopyAsset(source,Paths.Safe(Path.Combine(settings.JavaRoot,"assets","virtual",Paths.Id(asset["id"]!.GetValue<string>())),item.Key));
                    if(data["map_to_resources"]?.GetValue<bool>()==true)CopyAsset(source,Paths.Safe(Path.Combine(settings.JavaRoot,"resources"),item.Key));
                }
            }
            return lastResult;
        }
        return await DownloadAllAsync(jobs,progress,ct);
    }
    private InstallResult lastResult=new(0,0,0);
    private static void CopyAsset(string source,string destination){Directory.CreateDirectory(Path.GetDirectoryName(destination)!);if(!File.Exists(destination))File.Copy(source,destination);}
    private async Task<InstallResult> DownloadAllAsync(List<DownloadFile> all,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        // Prefer the last inherited definition when files resolve to the same target.
        var jobs=all.GroupBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.Last()).ToArray();
        int done=0,downloaded=0;long bytes=0;
        await System.Threading.Tasks.Parallel.ForEachAsync(jobs,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(settings.Parallel,1,12),CancellationToken=ct},async(file,token)=>{
            if(await dl.FileAsync(file,token)){Interlocked.Increment(ref downloaded);Interlocked.Add(ref bytes,new FileInfo(file.Path).Length);}
            int count=Interlocked.Increment(ref done);progress.Report(new($"校验 / 下载文件 · {Path.GetFileName(file.Path)}",count,jobs.Length));
        });
        lastResult=new(jobs.Length,downloaded,bytes);return lastResult;
    }
    public async Task<GameVersion> FabricAsync(GameVersion vanilla,IProgress<WorkProgress> progress,CancellationToken ct,string? requestedLoader=null)
    {
        await InstallAsync(vanilla,progress,ct);
        progress.Report(new("查询兼容的 Fabric Loader…"));
        var loaders=JsonNode.Parse(await dl.TextAsync("https://meta.fabricmc.net/v2/versions/loader/"+Uri.EscapeDataString(vanilla.Id),ct))!.AsArray();
        var loader=(requestedLoader!=null?loaders.FirstOrDefault(x=>x?["loader"]?["version"]?.GetValue<string>()==requestedLoader):loaders.FirstOrDefault(x=>x?["loader"]?["stable"]?.GetValue<bool>()==true)??loaders.FirstOrDefault())??throw new IOException("该版本还没有兼容的 Fabric Loader。");
        var loaderId=loader["loader"]!["version"]!.GetValue<string>();
        var text=await dl.TextAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(vanilla.Id)}/{Uri.EscapeDataString(loaderId)}/profile/json",ct);
        var json=JsonNode.Parse(text)!;var id=Paths.Id(json["id"]!.GetValue<string>());
        Settings.AtomicText(Paths.Safe(settings.JavaRoot,$"versions/{id}/{id}.json"),text);
        var version=new GameVersion(id,"Fabric",vanilla.Date,Folder:Paths.Safe(settings.JavaRoot,$"versions/{id}"));
        await InstallAsync(version,progress,ct);return version;
    }
}

public static class BedrockService
{
    public static string? FindEngine(Settings settings)=>Directory.Exists(settings.BedrockEngine)?Directory.EnumerateFiles(settings.BedrockEngine,"BedrockLauncher.UpdateProcessor.dll",SearchOption.AllDirectories).FirstOrDefault():null;
    public static string ArchiveRoot=>Path.Combine(Settings.DataPath,"bedrock-packages");
    public static IEnumerable<string> Packages()=>Directory.Exists(ArchiveRoot)?Directory.EnumerateFiles(ArchiveRoot).Where(x=>new[]{".appx",".msix",".appxbundle",".msixbundle",".msixvc"}.Contains(Path.GetExtension(x).ToLowerInvariant())):Array.Empty<string>();
    public static async Task<string> ImportAsync(string path,CancellationToken ct)
    {
        var ext=Path.GetExtension(path).ToLowerInvariant();
        if(!new[]{".appx",".msix",".appxbundle",".msixbundle",".msixvc"}.Contains(ext))throw new IOException("请选择 APPX、MSIX、Bundle 或 MSIXVC 安装包。");
        if(ext!=".msixvc")
        {
            using var zip=ZipFile.OpenRead(path);
            if(!zip.Entries.Any(x=>x.FullName.Equals("AppxManifest.xml",StringComparison.OrdinalIgnoreCase)||x.FullName.Equals("AppxMetadata/AppxBundleManifest.xml",StringComparison.OrdinalIgnoreCase)))throw new IOException("安装包缺少 Windows 应用清单。");
        }
        Directory.CreateDirectory(ArchiveRoot);var target=Paths.Safe(ArchiveRoot,Path.GetFileName(path));
        if(Path.GetFullPath(path).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))return target;
        if(File.Exists(target))throw new IOException("同名安装包已归档，原文件已保留。");
        var temp=target+".part";
        await using(var input=File.OpenRead(path))await using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,131072,true))await input.CopyToAsync(output,ct);
        File.Move(temp,target);return target;
    }
}
