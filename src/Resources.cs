using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlockDock;
public sealed record ResourceProject(string Id,string Slug,string Name,string Description,string Type,long Downloads,string Author,string IconUrl="")
{
    public string Count=>Downloads>=1000000?$"{Downloads/1000000.0:N1} 百万次下载":$"{Downloads:N0} 次下载";
    public string ProjectUrl=>"https://modrinth.com/"+Type+"/"+Uri.EscapeDataString(Slug);
}
public sealed record ResourceVersion(string Id,string ProjectId,string Name,string Number,string Type,string Date,JsonObject Data)
{
    public string Compatibility=>"Java "+string.Join(" / ",Data["game_versions"]?.AsArray().Select(x=>x?.ToString()).Take(4)??Array.Empty<string>())+" · "+string.Join(" / ",Data["loaders"]?.AsArray().Select(x=>x?.ToString())??Array.Empty<string>())+" · "+(Type=="release"?"稳定版":Type);
    public override string ToString()=>(Number==Name?Number:Number+" · "+Name)+" · "+Date+(Type=="release"?"":" · "+Type);
}
public sealed record InstalledResource(string File,string Kind,string State,string Path)
{
    LocalContentInfo Info=>ContentIcons.Content(Path,Kind=="模组"?"mod":Kind=="光影"?"shader":"resourcepack");
    public string Name=>Info.Name;
    public string Version=>Info.Version;
    public System.Windows.Media.ImageSource Icon=>Info.Icon;
    public override string ToString()=>File;
}
public sealed class ResourceService : IDisposable
{
    readonly Settings settings;readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(40)};readonly Downloader dl;
    const string Api="https://api.modrinth.com/v2";
    public Action<string>? Log{get;set;}
    public ResourceService(Settings settings){this.settings=settings;dl=new Downloader(settings.Source);http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockDock/2.0.0 (Minecraft desktop launcher)");}
    static string Query(object value)=>Uri.EscapeDataString(JsonSerializer.Serialize(value));
    async Task<JsonNode> GetAsync(string path,CancellationToken ct)=>JsonNode.Parse(await http.GetStringAsync(Api+path,ct))??throw new IOException("资源平台返回空数据。");
    public static string SearchPath(string query,string type,GameInstance? target,int offset,string sort="downloads")
    {
        var facets=new List<string[]>{new[]{"project_type:"+type}};
        if(target!=null&&type!="modpack")facets.Add(new[]{"versions:"+target.Minecraft});
        if(target!=null&&type=="mod"&&target.Loader!="vanilla")facets.Add(new[]{"categories:"+target.Loader});
        if(sort is not("downloads" or "updated" or "relevance"))throw new IOException("不支持这个排序方式。");return "/search?query="+Uri.EscapeDataString(query)+"&facets="+Query(facets)+"&index="+sort+"&limit=20&offset="+offset;
    }
    public async Task<(List<ResourceProject> Items,int Total)> SearchAsync(string query,string type,GameInstance? target,int offset,CancellationToken ct,string sort="downloads")
    {
        var data=await GetAsync(SearchPath(query,type,target,offset,sort),ct);
        return(data["hits"]!.AsArray().Select(x=>new ResourceProject(x!["project_id"]!.ToString(),x["slug"]!.ToString(),x["title"]!.ToString(),x["description"]?.ToString()??"",type,x["downloads"]!.GetValue<long>(),x["author"]?.ToString()??"",x["icon_url"]?.ToString()??"")).ToList(),data["total_hits"]!.GetValue<int>());
    }
    static ResourceVersion ToVersion(JsonNode x)=>new(x["id"]!.ToString(),x["project_id"]!.ToString(),x["name"]!.ToString(),x["version_number"]!.ToString(),x["version_type"]!.ToString(),x["date_published"]!.ToString().Split('T')[0],x.AsObject());
    public async Task<List<ResourceVersion>> VersionsAsync(string projectId,string type,GameInstance? game,CancellationToken ct)
    {
        string query="";if(game!=null&&type!="modpack")query="?game_versions="+Query(new[]{game.Minecraft});
        if(game!=null&&type=="mod"&&game.Loader!="vanilla")query+=(query.Length==0?"?":"&")+"loaders="+Query(new[]{game.Loader});
        return (await GetAsync("/project/"+Uri.EscapeDataString(projectId)+"/version"+query,ct)).AsArray().Select(x=>ToVersion(x!)).ToList();
    }
    public static string ResourceFolder(string type)=>type switch{"mod"=>"mods","resourcepack"=>"resourcepacks","shader"=>"shaderpacks",_=>throw new IOException("不支持的资源类型。")};
    static JsonNode PrimaryFile(ResourceVersion version,string type)
    {
        var files=version.Data["files"]!.AsArray();string ext=type=="modpack"?".mrpack":type=="mod"?".jar":".zip";
        return files.FirstOrDefault(x=>x?["primary"]?.GetValue<bool>()==true&&x["filename"]!.ToString().EndsWith(ext,StringComparison.OrdinalIgnoreCase))??files.FirstOrDefault(x=>x?["filename"]?.ToString().EndsWith(ext,StringComparison.OrdinalIgnoreCase)==true)??throw new IOException("这个发布版本没有可安装的 "+ext+" 文件。");
    }
    public static void CheckCompatibility(ResourceVersion version,GameInstance game)
    {
        if(version.Data["game_versions"] is JsonArray supported&&!supported.Any(x=>x?.ToString()==game.Minecraft))throw new IOException("这个文件不支持当前游戏版本 "+game.Minecraft+"。");
        if(version.Data["loaders"] is JsonArray loaders&&!loaders.Any(x=>x?.ToString()==game.Loader))throw new IOException("这个模组不适合当前加载器 "+game.Loader+"，请切换兼容版本。");
    }
    public async Task<int> InstallAsync(ResourceProject project,ResourceVersion version,GameInstance target,IProgress<WorkProgress> p,CancellationToken ct)
    {
        if(project.Type=="mod"&&target.Loader=="vanilla")throw new IOException("这个游戏是原版。请先在下载中心选择 Fabric、Forge 或 NeoForge 新建支持模组的游戏。");
        if(project.Type=="mod")CheckCompatibility(version,target);
        if(project.Type=="shader")
        {
            if(target.Loader is "fabric" or "quilt"){
                var iris=await VersionsAsync("iris","mod",target,ct);if(iris.Count==0)throw new IOException("当前游戏版本没有兼容的 Iris 光影支持。请选择支持 Iris 的 Fabric 版本。");
                await InstallGraphAsync(iris.FirstOrDefault(x=>x.Type=="release")??iris[0],"mod",target,p,ct,new(),new());
            }else throw new IOException("此版本暂提供 Fabric + Iris 的一键光影安装。请先新建 Fabric 游戏；Forge / NeoForge 请自行安装相应光影支持后导入文件。");
        }
        return await InstallGraphAsync(version,project.Type,target,p,ct,new(),new());
    }
    async Task<int> InstallGraphAsync(ResourceVersion version,string type,GameInstance target,IProgress<WorkProgress> p,CancellationToken ct,HashSet<string> seen,Dictionary<string,string> selected)
    {
        if(!seen.Add(version.Id))return 0;if(seen.Count>150)throw new IOException("模组依赖过多，已停止递归安装。");
        if(selected.TryGetValue(version.ProjectId,out var chosen)&&chosen!=version.Id)throw new IOException("依赖要求同一个模组的不同版本，请换一个兼容发布版本。");selected[version.ProjectId]=version.Id;
        if(type=="mod")CheckCompatibility(version,target);int count=0;
        if(type=="mod"&&version.Data["dependencies"] is JsonArray dependencies)
        foreach(var dependency in dependencies.Where(x=>x?["dependency_type"]?.ToString()=="required"))
        {
            ResourceVersion required;
            if(dependency!["version_id"]?.ToString() is string pinned)required=ToVersion(await GetAsync("/version/"+Uri.EscapeDataString(pinned),ct));
            else if(dependency["project_id"]?.ToString() is string id){var choices=await VersionsAsync(id,"mod",target,ct);required=choices.FirstOrDefault(x=>x.Type=="release")??choices.FirstOrDefault()??throw new IOException("必需前置模组没有兼容版本："+id);}
            else continue;
            count+=await InstallGraphAsync(required,"mod",target,p,ct,seen,selected);
        }
        var file=PrimaryFile(version,type);string filename=Paths.Id(file["filename"]!.ToString());string sha=file["hashes"]?["sha1"]?.ToString()??"";
        if(!Regex.IsMatch(sha,"^[0-9a-fA-F]{40}$"))throw new IOException("资源没有提供有效的 SHA-1，无法验证下载内容。");
        var destination=Paths.Safe(target.GameDirectory,ResourceFolder(type)+"/"+filename);p.Report(new("安装 "+version.Name));await dl.FileAsync(new(file["url"]!.ToString(),destination,sha,file["size"]!.GetValue<long>()),ct,p);
        var receiptsPath=Path.Combine(target.Folder,".blockdock-resources.json");var receipts=File.Exists(receiptsPath)?JsonNode.Parse(await File.ReadAllTextAsync(receiptsPath,ct))!.AsObject():new JsonObject();
        if(receipts[version.ProjectId]?["file"]?.ToString() is string old&&old!=filename){var prior=Paths.Safe(target.GameDirectory,ResourceFolder(type)+"/"+Paths.Id(old));if(File.Exists(prior)&&!File.Exists(prior+".disabled"))File.Move(prior,prior+".disabled");}
        receipts[version.ProjectId]=new JsonObject{["file"]=filename,["version"]=version.Id,["type"]=type,["name"]=version.Name,["sha1"]=sha};Settings.AtomicText(receiptsPath,receipts.ToJsonString());
        Log?.Invoke("已安装 "+version.Name+" → "+ResourceFolder(type));return count+1;
    }
    public async Task<GameInstance> InstallPackAsync(ResourceVersion version,string name,IProgress<WorkProgress> p,CancellationToken ct)
    {
        var file=PrimaryFile(version,"modpack");string package=Paths.Safe(Settings.DataPath,"downloads/"+version.Id+"-"+Paths.Id(file["filename"]!.ToString()));
        await dl.FileAsync(new(file["url"]!.ToString(),package,file["hashes"]!["sha1"]!.ToString(),file["size"]!.GetValue<long>()),ct,p);
        var game=await ImportPackAsync(package,name,p,ct);
        try{var project=await GetAsync("/project/"+Uri.EscapeDataString(version.ProjectId),ct);if(Uri.TryCreate(project["icon_url"]?.ToString(),UriKind.Absolute,out var icon)&&icon.Scheme=="https"){using var response=await http.GetAsync(icon,HttpCompletionOption.ResponseHeadersRead,ct);if(response.IsSuccessStatusCode&&response.Content.Headers.ContentLength is >0 and <2097152){var data=await response.Content.ReadAsByteArrayAsync(ct);await File.WriteAllBytesAsync(Path.Combine(game.Folder,".blockdock-icon.png"),data,ct);}}}catch(Exception e)when(!ct.IsCancellationRequested){Log?.Invoke("整合包图标暂未读取："+e.Message);}return game;
    }
    public async Task<GameInstance> ImportPackAsync(string package,string displayName,IProgress<WorkProgress> p,CancellationToken ct)
    {
        using var zip=ZipFile.OpenRead(package);if(zip.Entries.Count>100000||zip.Entries.Sum(x=>x.Length)>10L*1024*1024*1024)throw new IOException("整合包解压规模过大。");var entry=zip.GetEntry("modrinth.index.json")??throw new IOException("请选择 Modrinth 的 .mrpack 整合包。这个压缩包没有 modrinth.index.json。");
        if(entry.Length>10_000_000)throw new IOException("整合包清单过大。");
        JsonNode index;await using(var stream=entry.Open())index=await JsonNode.ParseAsync(stream,cancellationToken:ct)??throw new IOException("整合包清单为空。");
        if(index["formatVersion"]?.GetValue<int>()!=1||index["game"]?.ToString()!="minecraft")throw new IOException("不支持这个整合包格式。");
        var dependencies=index["dependencies"]!.AsObject();string minecraft=dependencies["minecraft"]?.ToString()??throw new IOException("整合包没有声明 Minecraft 版本。");
        string loader="vanilla",loaderVersion="";
        foreach(var dep in dependencies.Where(x=>x.Key!="minecraft")){
            string mapped=dep.Key switch{"fabric-loader"=>"fabric","forge"=>"forge","neoforge"=>"neoforge",_=>throw new IOException("这个整合包依赖尚不支持的加载器："+dep.Key)};
            if(loader!="vanilla")throw new IOException("整合包声明了多个加载器。");loader=mapped;loaderVersion=dep.Value!.ToString();
        }
        // Validate every destination before creating an instance or executing installers.
        var packFiles=index["files"]!.AsArray().Where(x=>x?["env"]?["client"]?.ToString() is not("unsupported" or "optional")).ToList();
        foreach(var f in packFiles){CheckPackPath(f!["path"]!.ToString());string h=f["hashes"]?["sha1"]?.ToString()??"";if(!Regex.IsMatch(h,"^[0-9a-fA-F]{40}$"))throw new IOException("整合包文件缺少有效校验值。");}
        foreach(var e in zip.Entries.Where(x=>x.FullName.StartsWith("overrides/")||x.FullName.StartsWith("client-overrides/")))if(e.Name.Length>0)CheckPackPath(e.FullName[(e.FullName.IndexOf('/')+1)..]);
        string name=string.IsNullOrWhiteSpace(displayName)?index["name"]?.ToString()??Path.GetFileNameWithoutExtension(package):displayName;
        string packHash;await using(var stream=File.OpenRead(package))packHash=Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream,ct));
        string jobKey=Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(packHash+name)));
        string jobFile=Paths.Safe(Settings.DataPath,"pack-jobs/"+jobKey+".json");GameInstance? game=null;
        if(File.Exists(jobFile)){var state=JsonNode.Parse(await File.ReadAllTextAsync(jobFile,ct))!;game=LaunchCore.Scan(settings).FirstOrDefault(x=>x.Id==state["id"]?.ToString());if(game!=null&&state["complete"]?.GetValue<bool>()==true)return game;}
        if(game is null){using var launch=new LaunchCore(settings){Log=Log};using var versionsDl=new Downloader(settings.Source);var versions=await new JavaService(settings,versionsDl).VersionsAsync(ct);var vanilla=versions.FirstOrDefault(x=>x.Id==minecraft)??throw new IOException("找不到整合包需要的游戏版本："+minecraft);game=await launch.InstallAsync(vanilla,loader,loaderVersion,name,p,ct);Settings.AtomicText(jobFile,JsonSerializer.Serialize(new{id=game.Id,complete=false}));}
        string pending=Path.Combine(game.Folder,".blockdock-pending");Settings.AtomicText(pending,"整合包安装尚未完成");
        Settings.AtomicText(Path.Combine(game.Folder,".blockdock-pack.json"),JsonSerializer.Serialize(new{package=Path.GetFullPath(package),name}));
        int done=0;
        foreach(var f in packFiles)
        {
            ct.ThrowIfCancellationRequested();string rel=f!["path"]!.ToString();var urls=f["downloads"]!.AsArray().Select(x=>x!.ToString()).ToArray();if(urls.Length==0)throw new IOException("整合包文件没有下载地址："+rel);Exception? last=null;bool complete=false;
            foreach(var url in urls){if(!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https")throw new IOException("整合包文件必须使用 HTTPS 下载。");try{await dl.FileAsync(new(url,Paths.Safe(game.GameDirectory,rel),f["hashes"]!["sha1"]!.ToString(),f["fileSize"]?.GetValue<long>()??0),ct);complete=true;break;}catch(Exception e)when(!ct.IsCancellationRequested){last=e;}}
            if(!complete)throw new IOException("整合包文件下载失败："+rel,last);p.Report(new("整合包文件 · "+rel,++done,packFiles.Count));
        }
        foreach(var prefix in new[]{"overrides/","client-overrides/"})foreach(var e in zip.Entries.Where(x=>x.FullName.StartsWith(prefix,StringComparison.Ordinal)&&x.Name.Length>0))
        {
            ct.ThrowIfCancellationRequested();string rel=e.FullName[prefix.Length..];var destination=Paths.Safe(game.GameDirectory,rel);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);await using var input=e.Open();await using var output=File.Create(destination);await input.CopyToAsync(output,ct);
        }
        File.Delete(pending);Settings.AtomicText(jobFile,JsonSerializer.Serialize(new{id=game.Id,complete=true}));return game with{Pending=false};
    }
    public static void CheckPackPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Any(x=>x is "." or ".."))throw new IOException("整合包包含不安全的路径。");
        var first=path.Split('/')[0];if(first.StartsWith(".blockdock-",StringComparison.OrdinalIgnoreCase)||new[]{"versions","libraries","runtime","data","engines"}.Contains(first,StringComparer.OrdinalIgnoreCase)||path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".ps1",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".bat",StringComparison.OrdinalIgnoreCase))throw new IOException("整合包包含不应写入的启动器或可执行文件："+path);
    }
    public static List<InstalledResource> Installed(GameInstance game,string type)
    {
        var folder=Path.Combine(game.GameDirectory,ResourceFolder(type));if(!Directory.Exists(folder))return new();
        return Directory.EnumerateFiles(folder).Where(x=>!x.EndsWith(".part")).Select(x=>new InstalledResource(Path.GetFileName(x),type switch{"mod"=>"模组","shader"=>"光影",_=>"材质"},x.EndsWith(".disabled")?"已停用":"已启用",x)).OrderBy(x=>x.File).ToList();
    }
    public static void Toggle(InstalledResource resource){string destination=resource.Path.EndsWith(".disabled")?resource.Path[..^9]:resource.Path+".disabled";if(File.Exists(destination))throw new IOException("目标文件已存在，已保留两份文件。");File.Move(resource.Path,destination);}
    public void Dispose(){http.Dispose();dl.Dispose();}
}
