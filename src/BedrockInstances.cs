using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BlockDock;

public sealed record BedrockInstance(string Name,string Version,bool Gdk,string Folder)
{
    // Kept for old extracted installations; new signed packages use Windows storage.
    public string GameFolder=>Path.Combine(Folder,"game");
    public string UserData=>Path.Combine(Folder,"user-data");
    public string Family=>Name+"_8wekyb3d8bbwe";
}

public static class BedrockInstances
{
    public static string Root=>Path.Combine(Settings.DataPath,"bedrock-instances");
    public static BedrockInstance For(BedrockPackage package)
    {
        var identity=BedrockNative.PackageIdentity(package);if(!Version.TryParse(identity.Version,out var version)||version.Revision<0)throw new IOException("安装包缺少有效的四段版本号。");
        bool gdk=package.Remote?.PackageType=="GDK";return new(identity.Name,version.ToString(),gdk,Paths.Safe(Root,identity.Name+"/"+version+"/"+(gdk?"gdk":"uwp")));
    }
    public static string LiveDirectory(string family,bool gdk)
    {
        if(family is not ("Microsoft.MinecraftUWP_8wekyb3d8bbwe" or "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe"))throw new IOException("无效的基岩版应用身份。");
        return gdk?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),family.StartsWith("Microsoft.MinecraftWindowsBeta_")?"Minecraft Bedrock Preview":"Minecraft Bedrock")
            :Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages",family,"LocalState","games","com.mojang");
    }
    static string LocalState(string family)=>Directory.GetParent(Directory.GetParent(LiveDirectory(family,false))!.FullName)!.FullName;
    static BedrockDataLink Link(string family,bool gdk)=>new(LiveDirectory(family,gdk),Root,Path.Combine(Settings.DataPath,"bedrock-isolation",family+(gdk?"-gdk":"-uwp")+".json"),systemPath:gdk?null:new BedrockSystemPath(LocalState(family)));
    public static void RequireGameClosed()
    {
        foreach(string name in new[]{"Minecraft.Windows","Minecraft.WindowsBeta"})foreach(var process in System.Diagnostics.Process.GetProcessesByName(name))using(process)if(!process.HasExited)throw new IOException("请先退出基岩版，再切换独立目录或恢复系统数据。");
    }
    public static void DetachForDeployment(string family)
    {
        RequireGameClosed();Link(family,false).DetachForDeployment();Link(family,true).DetachForDeployment();
    }
    public static void PrepareLaunch(BedrockInstance instance,CancellationToken ct)
    {
        RequireGameClosed();var link=Link(instance.Family,instance.Gdk);link.CheckTarget(instance.UserData);Directory.CreateDirectory(instance.UserData);
        if(!instance.Gdk)GrantPackage(instance.UserData,instance.Family);
        // Keep a newly started version from importing a different mode's live data.
        Link(instance.Family,!instance.Gdk).DetachForDeployment();link.Activate(instance.UserData,ct);
        Settings.AtomicText(Path.Combine(instance.Folder,".blockdock-instance.json"),System.Text.Json.JsonSerializer.Serialize(new{schema=1,instance.Name,instance.Version,instance.Gdk,instance.UserData}));
    }
    public static bool RestoreSystem(BedrockInstance instance,CancellationToken ct)
    {RequireGameClosed();return Link(instance.Family,instance.Gdk).Restore(ct);}
    static void GrantPackage(string directory,string family,string? localStateRoot=null)
    {
        string localState=localStateRoot??LocalState(family);var systemPath=new BedrockSystemPath(localState);
        string physicalLocalState=systemPath.Resolve(localState),physicalGames=systemPath.Resolve(Path.Combine(localState,"games"));
        // Validate both roots before adding any permissions. Only the known
        // Windows LocalState boundary may redirect; nested foreign links may not.
        BedrockDataLink.NoLinks(directory);BedrockDataLink.NoLinks(physicalGames);
        // Access belongs only to the selected AppContainer and current user;
        // never grant broad Authenticated Users permissions.
        int result=DeriveAppContainerSidFromAppContainerName(family,out var pointer);if(result<0)Marshal.ThrowExceptionForHR(result);
        try{var sid=new SecurityIdentifier(pointer);var sids=new[]{sid,new SecurityIdentifier("S-1-15-3-"+sid.Value["S-1-15-2-".Length..])};void Grant(string path,FileSystemRights rights,bool inherit){BedrockDataLink.NoLinks(path);var info=Directory.CreateDirectory(path);var acl=info.GetAccessControl();foreach(var identity in sids)acl.AddAccessRule(new FileSystemAccessRule(identity,rights,inherit?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));info.SetAccessControl(acl);}Grant(directory,FileSystemRights.Modify,true);Grant(physicalLocalState,FileSystemRights.ReadAndExecute,true);Grant(physicalGames,FileSystemRights.ReadAndExecute,true);}
        finally{FreeSid(pointer);}
    }
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
    [DllImport("advapi32.dll")]static extern IntPtr FreeSid(IntPtr sid);
}
