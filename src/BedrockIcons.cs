using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace BlockDock;
public static class BedrockIcons
{
    static readonly ConcurrentDictionary<string,ImageSource> cache=new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource Get(string path)
    {
        string key=path+File.GetLastWriteTimeUtc(path).Ticks;if(cache.TryGetValue(key,out var saved))return saved;
        try{using var zip=ZipFile.OpenRead(path);var manifest=zip.Entries.FirstOrDefault(x=>x.FullName.Equals("AppxManifest.xml",StringComparison.OrdinalIgnoreCase));if(manifest is {Length:>0 and <1048576}){using var stream=manifest.Open();using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});var xml=XDocument.Load(reader);var visual=xml.Descendants().FirstOrDefault(x=>x.Name.LocalName=="VisualElements");var logos=new[]{visual?.Attribute("Square150x150Logo")?.Value,visual?.Attribute("Square44x44Logo")?.Value,xml.Descendants().FirstOrDefault(x=>x.Name.LocalName=="Logo")?.Value};foreach(string? logo in logos){if(string.IsNullOrWhiteSpace(logo))continue;string name=logo.Replace('\\','/'),stem=name[..Math.Max(0,name.LastIndexOf('.'))];var entries=zip.Entries.Where(x=>x.FullName.Equals(name,StringComparison.OrdinalIgnoreCase)||x.FullName.StartsWith(stem+".",StringComparison.OrdinalIgnoreCase)&&x.FullName.EndsWith(".png",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.FullName.Equals(name,StringComparison.OrdinalIgnoreCase)).ThenByDescending(x=>x.Length);foreach(var entry in entries.Where(x=>x.Length is >0 and <1048576).Take(8)){using var imageStream=entry.Open();using var data=new MemoryStream();imageStream.CopyTo(data);data.Position=0;var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=96;image.StreamSource=data;image.EndInit();image.Freeze();cache[key]=image;return image;}}}}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or NotSupportedException or System.Runtime.InteropServices.COMException){}
        var fallback=InstanceIcons.Get("bedrock");cache[key]=fallback;return fallback;
    }
}
