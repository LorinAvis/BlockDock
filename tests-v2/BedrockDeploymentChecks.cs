using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

static class BedrockDeploymentChecks
{
    static void Check(bool good,string message){if(!good)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    static string Command(BedrockPackage package)
    {
        try{return (string)typeof(BedrockNative).GetMethod("SignedInstallCommand",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{package})!;}
        catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    static BedrockPackage Fixture(string folder,string name,bool signature=true,bool blockMap=true,bool emptySignature=false)
    {
        string path=Path.Combine(folder,name+".appx");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        {
            void Add(string entry,string text){using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write(text);}
            Add("AppxManifest.xml","<Package><Identity Name='Microsoft.MinecraftUWP' Version='1.18.0.2'/><Applications><Application Id='App'/></Applications></Package>");
            // Structural fixtures only: these bytes are NOT a valid signature.
            // Windows trust verification is intentionally never invoked here.
            if(signature)Add("AppxSignature.p7x",emptySignature?"":"structural fixture");if(blockMap)Add("AppxBlockMap.xml","<BlockMap/>");Add("payload.txt","preserve original payload");
        }
        return new("Fixture","1.18.0.2","APPX",path,null);
    }
    public static void Run()
    {
        string folder=Path.GetFullPath("work/bedrock-signed-deployment/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var package=Fixture(folder,"original ' $(Get-Date) ` signed");string hash=Hash(package.Path),command=Command(package);
        Check(command.StartsWith("Add-AppxPackage -Path '")&&command.Contains("original '' $(Get-Date) ` signed.appx'"),"signed deployment uses original package with a literal, escaped PowerShell path");
        Check(command.Contains("-ForceUpdateFromAnyVersion")&&!command.Contains("-Register")&&!command.Contains("-AllowUnsigned")&&!command.Contains("Remove-AppxPackage"),"signed version changes use Windows deployment without loose registration, unsigned bypass or prior uninstall");
        Check(Hash(package.Path)==hash&&!Directory.Exists(BedrockInstances.For(package).GameFolder),"deployment planning leaves signature, payload and original package bytes unchanged and never extracts game files");
        File.WriteAllText(Path.Combine(folder,"command.ps1"),command);
        foreach(var bad in new[]{Fixture(folder,"unsigned",false),Fixture(folder,"no-block-map",blockMap:false),Fixture(folder,"empty-signature",emptySignature:true)})
        {bool rejected=false;try{Command(bad);}catch(IOException){rejected=true;}Check(rejected,"incomplete original package rejected before deployment: "+Path.GetFileName(bad.Path));}
        bool missing=false;try{Command(package with{Path=Path.Combine(folder,"missing.appx")});}catch(IOException){missing=true;}Check(missing,"missing package is rejected before Windows changes");
        var instance=BedrockInstances.For(package);var installed=new WindowsGame(instance.Name,instance.Version,"full",instance.Family,@"C:\Program Files\WindowsApps\fixture","App");
        Check(!BedrockLaunchFlow.NeedsSwitch(package,installed),"exact signed version in WindowsApps does not require reinstallation into a private game directory");
        Check(BedrockLaunchFlow.NeedsSwitch(package,installed with{IsDevelopmentMode=true}),"old development registration is migrated even when its version matches");
        Check(BedrockLaunchFlow.NeedsSwitch(package,installed with{Version="1.21.0.0"})&&BedrockLaunchFlow.NeedsSwitch(package,installed with{Family="different-family"})&&BedrockLaunchFlow.NeedsSwitch(package,null),"other version, family and absent installation all require a switch");
        var gdk=new BedrockPackage("GDK Fixture","1.21.120.0","GDK",Path.Combine(folder,"signed.msixvc"),new BedrockVersion("1.21.120.0",0,"x64","GDK","",Array.Empty<string>(),"Microsoft.MinecraftUWP_1.21.120.0_x64__8wekyb3d8bbwe"));File.WriteAllText(gdk.Path,"opaque container fixture");Check(Command(gdk).Contains("signed.msixvc'")&&!Command(gdk).Contains("-Register"),"opaque GDK containers continue through Windows signed-package deployment");
        string? real=Environment.GetEnvironmentVariable("BLOCKDOCK_TEST_SIGNED_PACKAGE");if(!string.IsNullOrWhiteSpace(real)){string before=Hash(real);var local=new BedrockPackage("Local package","display-version","APPX",real,null);string manifestVersion;using(var archive=ZipFile.OpenRead(real))using(var stream=archive.GetEntry("AppxManifest.xml")!.Open())manifestVersion=System.Xml.Linq.XDocument.Load(stream).Descendants().First(x=>x.Name.LocalName=="Identity").Attribute("Version")!.Value;Check(Command(local).Contains("-Path")&&BedrockInstances.For(local).Version==manifestVersion&&Hash(real)==before,"existing local package passes read-only preflight using its manifest version, preserving all original bytes");}
        Console.WriteLine("PARSE_ONLY_COMMAND="+Path.Combine(folder,"command.ps1"));Console.WriteLine("BEDROCK SIGNED DEPLOYMENT CHECKS PASSED; no Windows package installation or system setting changes");
    }
}
