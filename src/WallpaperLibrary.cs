using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlockDock;
public sealed record LocalWallpaper(string Name,string Kind,string File,string Preview,string Project)
{
    public string Description=>Kind=="video"?"视频":"图片";
    public string Display=>Name+" · "+Description;
}
public static class WallpaperLibrary
{
    public static bool IsImage(string path)=>new[]{".png",".jpg",".jpeg",".bmp",".gif"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsVideo(string path)=>new[]{".mp4",".wmv",".m4v"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static LocalWallpaper? ReadProject(string path)
    {
        try{if(new FileInfo(path).Length>1024*1024)return null;var data=JsonNode.Parse(File.ReadAllText(path));string root=Path.GetDirectoryName(Path.GetFullPath(path))!;string file=LocalFile(root,data?["file"]?.ToString()??"");string kind=data?["type"]?.ToString()?.ToLowerInvariant()??"";if(kind=="video"&&!IsVideo(file))return null;if(IsImage(file))kind="image";if(kind is not("image" or "video")||!File.Exists(file))return null;return new(data?["title"]?.ToString()??Path.GetFileName(root),kind,file,"",path);}catch(Exception e)when(e is IOException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException){return null;}
    }
    static string LocalFile(string root,string relative){if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))return "";try{return Paths.Safe(root,relative);}catch(IOException){return "";}}
    public static bool IsScenePreview(string file){try{string root=Path.GetDirectoryName(Path.GetFullPath(file))!;string project=Path.Combine(root,"project.json");if(!File.Exists(project)||new FileInfo(project).Length>1048576)return false;var data=JsonNode.Parse(File.ReadAllText(project));string preview=LocalFile(root,data?["preview"]?.ToString()??"preview.jpg");return file.Equals(preview,StringComparison.OrdinalIgnoreCase)&&data?["type"]?.ToString() is not("image" or "video");}catch{return false;}}
    public static List<LocalWallpaper> Scan(string selectedRoot="")
    {
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);void Add(string p){if(Directory.Exists(p))roots.Add(Path.GetFullPath(p));}
        if(selectedRoot.Length>0)Add(selectedRoot);
        string steam=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath","")?.ToString()??"";
        var libraries=new HashSet<string>(StringComparer.OrdinalIgnoreCase);if(steam.Length>0)libraries.Add(steam);
        if(steam.Length>0){string config=Path.Combine(steam,"steamapps","libraryfolders.vdf");if(File.Exists(config)&&new FileInfo(config).Length<1024*1024)foreach(Match m in Regex.Matches(File.ReadAllText(config),"\"path\"\\s+\"([^\"]+)\""))libraries.Add(m.Groups[1].Value.Replace("\\\\","\\"));}
        foreach(string library in libraries){Add(Path.Combine(library,"steamapps","workshop","content","431960"));string engine=Path.Combine(library,"steamapps","common","wallpaper_engine");Add(Path.Combine(engine,"projects","myprojects"));Add(Path.Combine(engine,"projects","defaultprojects"));}
        if(selectedRoot.Length>0){Add(Path.Combine(selectedRoot,"projects","myprojects"));Add(Path.Combine(selectedRoot,"projects","defaultprojects"));Add(Path.Combine(selectedRoot,"steamapps","workshop","content","431960"));string? common=Directory.GetParent(selectedRoot)?.Name=="common"?Directory.GetParent(selectedRoot)?.Parent?.FullName:null;if(common!=null)Add(Path.Combine(common,"workshop","content","431960"));}
        var projects=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string root in roots){try{string direct=Path.Combine(root,"project.json");if(File.Exists(direct))projects.Add(direct);foreach(string child in Directory.EnumerateDirectories(root)){string p=Path.Combine(child,"project.json");if(File.Exists(p))projects.Add(p);}}catch(UnauthorizedAccessException){}catch(IOException){}}
        return projects.Select(ReadProject).OfType<LocalWallpaper>().OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToList();
    }
}
