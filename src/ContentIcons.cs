using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;
public sealed record LocalContentInfo(string Name,string Version,ImageSource Icon);
public static class ContentIcons
{
    static readonly ConcurrentDictionary<string,LocalContentInfo> contents=new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string,ImageSource> images=new(StringComparer.OrdinalIgnoreCase);
    internal static BitmapSource? Read(Stream stream)
    {
        try{long start=stream.Position;var frame=BitmapFrame.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);int width=frame.PixelWidth,height=frame.PixelHeight;if(width<1||height<1||width>8192||height>8192||(long)width*height>16777216)return null;stream.Position=start;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.StreamSource=stream;bitmap.CacheOption=BitmapCacheOption.OnLoad;if(width>256||height>256){if(width>=height)bitmap.DecodePixelWidth=256;else bitmap.DecodePixelHeight=256;}bitmap.EndInit();bitmap.Freeze();return bitmap;}catch{return null;}
    }
    public static ImageSource? FileImage(string path){try{if(!File.Exists(path)||new FileInfo(path).Length>4*1024*1024)return null;string key=path+File.GetLastWriteTimeUtc(path).Ticks;if(images.TryGetValue(key,out var cached))return cached;using var stream=File.OpenRead(path);var image=Read(stream);if(image!=null)images[key]=image;return image;}catch{return null;}}
    static string? TextEntry(ZipArchive zip,string path){var entry=zip.GetEntry(path);if(entry is null||entry.Length>512*1024)return null;using var reader=new StreamReader(entry.Open());return reader.ReadToEnd();}
    static ImageSource? ZipImage(ZipArchive zip,string? path){if(string.IsNullOrWhiteSpace(path))return null;var entry=zip.GetEntry(path.Replace('\\','/').TrimStart('/'));if(entry is null||entry.Length>1024*1024)return null;using var input=entry.Open();using var data=new MemoryStream();input.CopyTo(data);data.Position=0;return Read(data);}
    public static LocalContentInfo Content(string path,string kind)
    {
        string key=path+File.GetLastWriteTimeUtc(path).Ticks;if(contents.TryGetValue(key,out var saved))return saved;string name=Path.GetFileName(path),version="";ImageSource? icon=null;
        try{using var zip=ZipFile.OpenRead(path);string? raw=TextEntry(zip,"fabric.mod.json"),logo=null;if(raw!=null){var data=JsonNode.Parse(raw);name=data?["name"]?.ToString()??name;version=data?["version"]?.ToString()??"";var entry=data?["icon"];logo=entry is JsonObject o?o.OrderByDescending(x=>int.TryParse(x.Key,out var n)?n:0).FirstOrDefault().Value?.ToString():entry?.ToString();}
            else if((raw=TextEntry(zip,"quilt.mod.json"))!=null){var data=JsonNode.Parse(raw)?["quilt_loader"];name=data?["metadata"]?["name"]?.ToString()??name;version=data?["version"]?.ToString()??"";logo=data?["metadata"]?["icon"]?.ToString();}
            else if((raw=TextEntry(zip,"META-INF/neoforge.mods.toml")??TextEntry(zip,"META-INF/mods.toml"))!=null){name=Regex.Match(raw,"displayName\\s*=\\s*\"([^\"]+)\"").Groups[1].Value is string n&&n.Length>0?n:name;version=Regex.Match(raw,"version\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;logo=Regex.Match(raw,"logoFile\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;}
            else if((raw=TextEntry(zip,"mcmod.info"))!=null){var data=JsonNode.Parse(raw);if(data is JsonArray a)data=a.FirstOrDefault();else data=data?["modList"]?.AsArray().FirstOrDefault();name=data?["name"]?.ToString()??name;version=data?["version"]?.ToString()??"";logo=data?["logoFile"]?.ToString();}
            icon=ZipImage(zip,logo)??ZipImage(zip,"pack.png")??ZipImage(zip,"icon.png");if(version.Contains("${"))version="";
        }catch(IOException){}catch(InvalidDataException){}catch(System.Text.Json.JsonException){}catch(InvalidOperationException){}
        var result=new LocalContentInfo(name,version,icon??InstanceIcons.Get(kind));contents[key]=result;return result;
    }
    public static ImageSource Instance(GameInstance game)
    {
        foreach(string file in new[]{".blockdock-icon.png","icon.png","pack.png","instance.png","PCL/Logo.png","PCL/Logo.jpg"})if(FileImage(Path.Combine(game.Folder,file)) is ImageSource custom)return custom;
        string setup=Path.Combine(game.Folder,"PCL","Setup.ini");try{if(File.Exists(setup))foreach(string line in System.IO.File.ReadLines(setup).Where(x=>x.StartsWith("Logo:",StringComparison.OrdinalIgnoreCase))){string path=line[5..].Trim();if(Path.IsPathRooted(path)&&FileImage(path) is ImageSource icon)return icon;}}catch(IOException){}
        string key=game.Folder+"|"+game.Loader;if(images.TryGetValue(key,out var cached))return cached;
        try{
            string root=Directory.GetParent(Directory.GetParent(game.Folder)!.FullName)!.FullName;
            if(game.Loader!="vanilla"){
                string library=game.Loader switch{"fabric"=>"net/fabricmc/fabric-loader","neoforge"=>"net/neoforged/neoforge","forge"=>"net/minecraftforge/forge",_=>""};string folder=Path.Combine(root,"libraries",library);if(library.Length>0&&Directory.Exists(folder))foreach(string file in Directory.EnumerateFiles(folder,"*.jar",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(8)){using var zip=ZipFile.OpenRead(file);foreach(string path in new[]{"assets/fabricloader/icon.png","assets/forge/logo.png","forge_logo.png","neoforged_logo.png","assets/neoforge/neoforged_logo.png","icon.png"})if(ZipImage(zip,path) is ImageSource loader){images[key]=loader;return loader;}}
                var loaderFallback=InstanceIcons.Get(game.Loader);images[key]=loaderFallback;return loaderFallback;
            }
            string id=game.Id;var seen=new HashSet<string>();while(seen.Add(id)&&seen.Count<16){string folder=Path.Combine(root,"versions",id),jar=Path.Combine(folder,id+".jar"),json=Path.Combine(folder,id+".json");var candidates=new List<BitmapSource>();var paths=new[]{"icons/icon_256x256.png","icons/icon_128x128.png","icons/icon_64x64.png","icons/icon_32x32.png","icons/icon_16x16.png"};if(File.Exists(jar)){using var zip=ZipFile.OpenRead(jar);foreach(string path in paths.SelectMany(p=>new[]{p,"assets/minecraft/"+p}))if(ZipImage(zip,path) is BitmapSource icon)candidates.Add(icon);}var metadata=File.Exists(json)?JsonNode.Parse(System.IO.File.ReadAllText(json)):null;if(metadata?["assetIndex"]?["id"]?.ToString() is string indexId){string indexFile=Paths.Safe(root,"assets/indexes/"+Paths.Id(indexId)+".json");if(File.Exists(indexFile)){var index=JsonNode.Parse(System.IO.File.ReadAllText(indexFile));foreach(string iconPath in paths.SelectMany(p=>new[]{p,"minecraft/"+p}))if(index?["objects"]?[iconPath]?["hash"]?.ToString() is string hash&&Regex.IsMatch(hash,"^[a-fA-F0-9]{40}$")&&FileImage(Path.Combine(root,"assets","objects",hash[..2],hash)) is BitmapSource icon)candidates.Add(icon);}}if(candidates.OrderByDescending(x=>(long)x.PixelWidth*x.PixelHeight).FirstOrDefault() is BitmapSource best){images[key]=best;return best;}string? parent=metadata?["inheritsFrom"]?.ToString();if(parent==null)break;id=Paths.Id(parent);}
        }catch(IOException){}catch(System.Text.Json.JsonException){}catch(UnauthorizedAccessException){}
        var fallback=InstanceIcons.Get(game.Loader);images[key]=fallback;return fallback;
    }
}
