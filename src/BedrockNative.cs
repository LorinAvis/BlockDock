using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BlockDock;
public sealed record WindowsGame(string Name,string Version,string FullName,string Family,string Location,string AppId,bool IsDevelopmentMode=false);
public sealed record BedrockPackage(string Name,string Version,string Type,string Path,BedrockVersion? Remote)
{
    public string Summary=>Version+" · "+Type;
    public override string ToString()=>Version+" · 基岩版独立实例";
    public System.Windows.Media.ImageSource Icon=>BedrockIcons.Get(Path);
}
public static class BedrockNative
{
    static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    static void CheckFamily(string name){if(name is not("Microsoft.MinecraftUWP" or "Microsoft.MinecraftWindowsBeta"))throw new IOException("只允许管理 Minecraft 官方 Windows 应用。");}
    static async Task<string> ShellAsync(string command,CancellationToken ct)
    {
        string script="$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "+command;
        var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)??throw new IOException("无法调用 Windows 安装服务。");var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);string value=await output,err=await error;
        if(process.ExitCode!=0)throw new IOException("Windows 未能完成操作：\n"+err.Trim());return value.Trim();
    }
    public static async Task<List<WindowsGame>> InstalledAsync(CancellationToken ct)
    {
        string script="@('Microsoft.MinecraftUWP','Microsoft.MinecraftWindowsBeta') | ForEach-Object { Get-AppxPackage -Name $_ } | ForEach-Object { $p=$_; $m=Get-AppxPackageManifest -Package $p.PackageFullName; [pscustomobject]@{Name=$p.Name;Version=$p.Version.ToString();FullName=$p.PackageFullName;Family=$p.PackageFamilyName;Location=$p.InstallLocation;AppId=(@($m.Package.Applications.Application)[0].Id);IsDevelopmentMode=$p.IsDevelopmentMode} } | ConvertTo-Json -Compress";
        string text=await ShellAsync(script,ct);if(string.IsNullOrWhiteSpace(text))return new();var data=JsonNode.Parse(text)!;var rows=data is JsonArray a?a:new JsonArray(data);
        return rows.Select(x=>new WindowsGame(x!["Name"]!.ToString(),x["Version"]!.ToString(),x["FullName"]!.ToString(),x["Family"]!.ToString(),x["Location"]!.ToString(),x["AppId"]?.ToString()??"Game",x["IsDevelopmentMode"]?.GetValue<bool>()??false)).ToList();
    }
    public static List<BedrockPackage> Packages()
    {
        var result=new List<BedrockPackage>();foreach(var file in BedrockService.Packages())
        {
            try{var receipt=JsonNode.Parse(File.ReadAllText(file+".receipt.json"))!;string version=receipt["Id"]!.ToString(),type=receipt["PackageType"]!.ToString();int channel=receipt["Channel"]?.GetValue<int>()??0;var remote=new BedrockVersion(version,channel,receipt["Architecture"]?.ToString()??"x64",type,receipt["UpdateId"]?.ToString()??"",Array.Empty<string>(),receipt["Identity"]?.ToString()??"");result.Add(new("Minecraft "+version,version,type,file,remote));}
            catch{var local=new BedrockPackage(Path.GetFileName(file),"本地安装包",Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),file,null);try{var identity=PackageIdentity(local);local=local with{Name="Minecraft "+identity.Version,Version=identity.Version};}catch(IOException){}catch(InvalidDataException){}catch(System.Xml.XmlException){}catch(InvalidOperationException){}result.Add(local);}
        }
        return result.OrderByDescending(x=>x.Version).ToList();
    }
    internal static (string Name,string Version,string AppId) PackageIdentity(BedrockPackage package)
    {
        if(package.Remote?.PackageType=="GDK"){
            var parts=package.Remote.Identity.Split('_');if(parts.Length<5)throw new IOException("GDK 安装包缺少精确身份信息。");CheckFamily(parts[0]);return(parts[0],parts[1],"Game");
        }
        using var archive=ZipFile.OpenRead(package.Path);var entry=archive.GetEntry("AppxManifest.xml")??throw new IOException("该安装包不是可直接管理的 Minecraft APPX。");if(entry.Length>1024*1024)throw new IOException("安装包身份文件过大。");using var stream=entry.Open();using var reader=System.Xml.XmlReader.Create(stream,new(){DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});var xml=XDocument.Load(reader);var identity=xml.Root!.Elements().First(x=>x.Name.LocalName=="Identity");string name=identity.Attribute("Name")!.Value;CheckFamily(name);return(name,identity.Attribute("Version")!.Value,xml.Descendants().First(x=>x.Name.LocalName=="Application").Attribute("Id")!.Value);
    }
    public static async Task<WindowsGame?> CurrentForAsync(BedrockPackage package,CancellationToken ct){var identity=PackageIdentity(package);return(await InstalledAsync(ct)).FirstOrDefault(x=>x.Name==identity.Name);}
    public static async Task<string?> BackupWorldsAsync(string family,IProgress<WorkProgress> progress,CancellationToken ct)
    {
        if(!family.EndsWith("_8wekyb3d8bbwe",StringComparison.Ordinal)||!family.StartsWith("Microsoft.Minecraft",StringComparison.Ordinal))throw new IOException("无效的 Minecraft 应用身份。");
        var candidates=new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages",family,"LocalState","games","com.mojang","minecraftWorlds"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Minecraft Bedrock","Users")};
        var files=new List<(string File,string Entry)>();
        for(int i=0;i<candidates.Length;i++)if(Directory.Exists(candidates[i]))foreach(var file in Directory.EnumerateFiles(candidates[i],"*",SearchOption.AllDirectories)){
            if(i==1&&!file.Contains(Path.DirectorySeparatorChar+"minecraftWorlds"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
            files.Add((file,i+"/"+Path.GetRelativePath(candidates[i],file).Replace('\\','/')));
        }
        if(files.Count==0)return null;
        string target=Paths.Safe(Settings.DataPath,"backups/Bedrock_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await Task.Run(()=>{using(var zip=ZipFile.Open(target+".part",ZipArchiveMode.Create))for(int i=0;i<files.Count;i++){ct.ThrowIfCancellationRequested();zip.CreateEntryFromFile(files[i].File,files[i].Entry,CompressionLevel.Fastest);progress.Report(new("备份基岩版世界…",i+1,files.Count));}File.Move(target+".part",target);},ct);return target;
    }
    internal static string SignedInstallCommand(BedrockPackage package)
    {
        string path=Path.GetFullPath(package.Path);
        if(!File.Exists(path))throw new IOException("原始安装包不存在，请重新下载或导入。");
        if(package.Remote?.PackageType!="GDK")
        {
            using var archive=ZipFile.OpenRead(path);
            if(archive.GetEntry("AppxSignature.p7x") is not {Length:>0}||archive.GetEntry("AppxBlockMap.xml") is not {Length:>0})
                throw new IOException("安装包缺少原始签名或文件校验表。请重新下载完整的官方签名包；当前游戏未卸载。");
        }
        // This structural check does not verify trust: Windows validates the
        // original signature, payload and licence during signed deployment.
        // Updating/downgrading through one deployment also avoids uninstalling a
        // working signed version before Windows has accepted the replacement.
        return "Add-AppxPackage -Path "+Quote(path)+" -ForceUpdateFromAnyVersion -ForceApplicationShutdown";
    }
    public static async Task InstallAsync(BedrockPackage package,bool allowReplace,IProgress<WorkProgress> p,CancellationToken ct,Action? deploymentStarting=null)
    {
        BedrockInstances.RequireGameClosed();var instance=BedrockInstances.For(package);
        var identity=PackageIdentity(package);var installed=(await InstalledAsync(ct)).FirstOrDefault(x=>x.Name==identity.Name);
        if(!BedrockLaunchFlow.NeedsSwitch(package,installed))return;
        if(installed!=null&&!allowReplace)throw new IOException("Windows 同一频道一次只保留一个版本。请确认备份后再切换。");
        string command=SignedInstallCommand(package);
        if(installed!=null){p.Report(new("备份原有世界…"));await BackupWorldsAsync(installed.Family,p,ct);}
        // Once Windows deployment begins, let the OS finish it rather than
        // interrupting registration in the middle. Worlds have already been backed up.
        ct.ThrowIfCancellationRequested();deploymentStarting?.Invoke();BedrockInstances.DetachForDeployment(instance.Family);p.Report(new("Windows 正在安装所选版本，请稍候…"));
        // Only an old loose development registration needs explicit removal.
        // Normal signed installations stay in place until Windows replaces them.
        if(installed?.IsDevelopmentMode==true)await ShellAsync("Remove-AppxPackage -Package "+Quote(installed.FullName)+" -PreserveApplicationData",CancellationToken.None);
        await ShellAsync(command,CancellationToken.None);
        var result=(await InstalledAsync(default)).FirstOrDefault(x=>x.Name==identity.Name);if(BedrockLaunchFlow.NeedsSwitch(package,result))throw new IOException("Windows 返回的安装版本与所选版本不一致，已停止启动。");
    }
    public static async Task<Process> LaunchAsync(BedrockPackage package,CancellationToken ct,IProgress<WorkProgress>? progress=null)
    {
        var identity=PackageIdentity(package);var instance=BedrockInstances.For(package);var installed=(await InstalledAsync(ct)).FirstOrDefault(x=>x.Name==identity.Name&&x.Version==identity.Version)??throw new IOException("请先安装 / 切换到所选版本，再点击开始游戏。");
        if(BedrockLaunchFlow.NeedsSwitch(package,installed))throw new IOException("请先安装 / 切换所选版本，以使用原始签名包正常安装。");
        await Task.Run(()=>BedrockInstances.PrepareLaunch(instance,ct),ct);ct.ThrowIfCancellationRequested();
        uint activated;var manager=(IApplicationActivationManager)new ApplicationActivationManager();try{int hr=manager.ActivateApplication(installed.Family+"!"+installed.AppId,null,0,out activated);if(hr<0)Marshal.ThrowExceptionForHR(hr);}finally{Marshal.ReleaseComObject(manager);}
        progress?.Report(new("启动请求已发送，等待实际基岩版进程…"));var until=DateTimeOffset.UtcNow.AddSeconds(60);
        while(DateTimeOffset.UtcNow<until)
        {
            ct.ThrowIfCancellationRequested();var candidates=new List<Process>();try{if(activated>0)candidates.Add(Process.GetProcessById(checked((int)activated)));}catch(ArgumentException){}foreach(string name in new[]{"Minecraft.Windows","Minecraft.WindowsBeta"})candidates.AddRange(Process.GetProcessesByName(name));Process? match=null;
            foreach(var candidate in candidates){try{if(match==null&&!candidate.HasExited&&candidate.ProcessName is "Minecraft.Windows" or "Minecraft.WindowsBeta"&&PackageFullName(candidate.Id)==installed.FullName){match=candidate;continue;}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}candidate.Dispose();}if(match!=null)return match;await Task.Delay(200,ct);
        }
        throw new IOException("Windows 已接受启动请求，但尚未检测到所选版本的游戏进程。请检查游戏或 Xbox 的启动提示；本程序没有将请求成功当作游戏已启动。");
    }
    static string PackageFullName(int pid)
    {
        using var handle=OpenProcess(0x1000,false,pid);if(handle.IsInvalid)return "";uint length=0;if(GetPackageFullName(handle,ref length,null)!=122||length>4096)return "";var value=new StringBuilder((int)length);return GetPackageFullName(handle,ref length,value)==0?value.ToString():"";
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackageFullName(Microsoft.Win32.SafeHandles.SafeProcessHandle process,ref uint length,StringBuilder? fullName);
    [ComImport,Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]private class ApplicationActivationManager{}
    [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IApplicationActivationManager{[PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.LPWStr)]string? arguments,int options,out uint pid);}
}
