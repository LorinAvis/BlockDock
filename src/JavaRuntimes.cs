using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlockDock;
public sealed record JavaRuntime(string Path,int Major,string Version,string Architecture)
{
    public string Label=>$"Java {Version} · {Architecture}";
    public override string ToString()=>Label+" · "+Path;
}
public static class JavaRuntimes
{
    public static JavaRuntime? Inspect(string path)
    {
        try{if(!File.Exists(path))return null;string release=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path))!,"release");string text=File.Exists(release)?File.ReadAllText(release):"";string version=Regex.Match(text,"JAVA_VERSION=\"([^\"]+)\"").Groups[1].Value;if(version.Length==0)version=System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion??"";var m=Regex.Match(version,@"^(?:1\.)?(\d+)");if(!int.TryParse(m.Groups[1].Value,out int major)||major<6)return null;string arch=Regex.Match(text,"OS_ARCH=\"([^\"]+)\"").Groups[1].Value;return new(System.IO.Path.GetFullPath(path),major,version,arch.Length>0?arch:"未知架构");}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
    }
    public static List<JavaRuntime> Detect()
    {
        var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);void Add(string p){if(File.Exists(p))candidates.Add(System.IO.Path.GetFullPath(p));}
        foreach(string env in new[]{"JAVA_HOME","JDK_HOME"})if(Environment.GetEnvironmentVariable(env) is string root)Add(System.IO.Path.Combine(root,"bin","java.exe"));
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(';'))try{if(folder.Length>0)Add(System.IO.Path.Combine(folder.Trim('"'),"java.exe"));}catch(ArgumentException){}
        foreach(string key in new[]{@"HKEY_LOCAL_MACHINE\SOFTWARE\JavaSoft\Java Runtime Environment",@"HKEY_LOCAL_MACHINE\SOFTWARE\JavaSoft\JDK",@"HKEY_LOCAL_MACHINE\SOFTWARE\JavaSoft\Java Development Kit"})try{string? version=Registry.GetValue(key,"CurrentVersion",null)?.ToString();if(version!=null&&Registry.GetValue(key+"\\"+version,"JavaHome",null) is string java)Add(System.IO.Path.Combine(java,"bin","java.exe"));}catch(System.Security.SecurityException){}
        var roots=new[]{System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Java"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Eclipse Adoptium"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Microsoft"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".jdks"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),".minecraft","runtime"),System.IO.Path.Combine(Settings.DataPath,"runtime")};
        void Walk(string root,int depth){if(depth<0||!Directory.Exists(root))return;try{Add(System.IO.Path.Combine(root,"bin","java.exe"));Add(System.IO.Path.Combine(root,"java.exe"));foreach(var child in Directory.EnumerateDirectories(root))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)Walk(child,depth-1);}catch(IOException){}catch(UnauthorizedAccessException){}}
        foreach(string root in roots)Walk(root,5);return candidates.Select(Inspect).OfType<JavaRuntime>().DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Major).ThenBy(x=>x.Path).ToList();
    }
    public static JavaRuntime? Select(IEnumerable<JavaRuntime> values,int required)=>values.FirstOrDefault(x=>x.Major==required&&x.Architecture is "amd64" or "x86_64" or "aarch64")??values.FirstOrDefault(x=>x.Major==required);
}
