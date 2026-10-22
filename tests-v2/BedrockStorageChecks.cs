using BlockDock;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

static class BedrockStorageChecks
{
    static void Check(bool good,string label){if(!good)throw new Exception("FAIL: "+label);Console.WriteLine("PASS: "+label);}
    static void Rejected(Action action,string label){bool rejected=false;try{action();}catch(IOException){rejected=true;}Check(rejected,label);}
    static void Grant(string userData,string localState)
    {
        try{typeof(BedrockInstances).GetMethod("GrantPackage",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{userData,"Microsoft.MinecraftUWP_8wekyb3d8bbwe",localState});}
        catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    public static void Run()
    {
        string root=Path.GetFullPath("work/bedrock-storage-checks/自定义 目录 ' "+Guid.NewGuid().ToString("N"));if(!root.StartsWith(Path.GetFullPath("work")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture path must remain in workspace work directory.");Directory.CreateDirectory(root);
        string logical=Path.Combine(root,"用户 目录","Packages","Minecraft fixture","LocalState"),physical=Path.Combine(root,"另一个 存储卷","WpSystem","LocalState"),owned=Path.Combine(root,"启动器 数据","instances"),one=Path.Combine(owned,"1.18.2.0","user-data"),two=Path.Combine(owned,"1.21.0.0","user-data"),journal=Path.Combine(root,"记录","uwp.json"),live=Path.Combine(logical,"games","com.mojang");
        Directory.CreateDirectory(Path.Combine(physical,"games","com.mojang","minecraftWorlds","original"));File.WriteAllText(Path.Combine(physical,"games","com.mojang","minecraftWorlds","original","level.dat"),"preserve-original");Directory.CreateDirectory(Path.GetDirectoryName(logical)!);DirectoryJunction.Create(logical,physical);
        Rejected(()=>new BedrockDataLink(live,owned,journal).Activate(one),"fixture reproduces the old LocalState junction rejection before data moves");
        Check(File.ReadAllText(Path.Combine(live,"minecraftWorlds","original","level.dat"))=="preserve-original","rejected old path check leaves original fixture world untouched");
        var mapping=new BedrockSystemPath(logical);Check(mapping.Resolve(Path.Combine(logical,"games"))==Path.Combine(physical,"games"),"known Windows LocalState root resolves to its real app storage directory");
        Rejected(()=>mapping.Resolve(Path.Combine(logical,"..","unrelated")),"system path resolution rejects paths outside its captured LocalState root");
        Grant(one,logical);var rules=new DirectoryInfo(one).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();Check(rules.Count(x=>x.IdentityReference.Value.StartsWith("S-1-15-"))==2&&rules.All(x=>x.IdentityReference.Value!="S-1-5-11"),"relocated LocalState grants only selected package identities using actual directories");
        var link=new BedrockDataLink(live,owned,journal,systemPath:mapping);link.Activate(one);Check(!Directory.EnumerateFileSystemEntries(one).Any(),"relocated Windows storage starts first custom-path instance with empty data");File.WriteAllText(Path.Combine(live,"options.txt"),"version-one");link.Activate(two);Check(!File.Exists(Path.Combine(live,"options.txt")),"second custom-path version remains isolated across the Windows storage junction");File.WriteAllText(Path.Combine(live,"options.txt"),"version-two");link.DetachForDeployment();Check(File.ReadAllText(Path.Combine(two,"options.txt"))=="version-two","deployment detaches only the inner managed data link under relocated storage");
        // A fresh object models reopening the launcher with the same logical
        // journal paths: no migration to a machine-specific physical drive.
        link=new BedrockDataLink(live,owned,journal,systemPath:new BedrockSystemPath(logical));link.Activate(one);Check(File.ReadAllText(Path.Combine(live,"options.txt"))=="version-one"&&JsonNode.Parse(File.ReadAllText(journal))!["Live"]!.ToString()==live,"reopening preserves version data and logical journal identity");
        Check(link.Restore()&&File.ReadAllText(Path.Combine(live,"minecraftWorlds","original","level.dat"))=="preserve-original"&&File.ReadAllText(Path.Combine(two,"options.txt"))=="version-two","restore recovers original worlds through relocated storage and retains both instances");Check(new DirectoryInfo(logical).ResolveLinkTarget(true)!.FullName==physical,"Windows LocalState junction itself is never removed or retargeted");
        string nested=Path.Combine(physical,"games"),retained=Path.Combine(root,"保留 games"),foreign=Path.Combine(root,"其他 程序目录");Directory.Move(nested,retained);Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"keep.txt"),"untouched");DirectoryJunction.Create(nested,foreign);
        Rejected(()=>new BedrockDataLink(live,owned,journal,systemPath:new BedrockSystemPath(logical)).Activate(one),"unrelated nested games junction is still rejected despite allowed LocalState relocation");Rejected(()=>Grant(one,logical),"permission preparation also rejects unrelated nested system links");Check(File.ReadAllText(Path.Combine(foreign,"keep.txt"))=="untouched","foreign linked storage remains untouched");Directory.Delete(nested,false);Directory.Move(retained,nested);
        string ownedAlias=Path.Combine(root,"linked-instance-root");DirectoryJunction.Create(ownedAlias,owned);Rejected(()=>new BedrockDataLink(live,ownedAlias,Path.Combine(root,"bad.json"),systemPath:mapping).Activate(Path.Combine(ownedAlias,"escape","user-data")),"allowing Windows storage does not allow linked instance roots");Directory.Delete(ownedAlias,false);
        string otherPhysical=Path.Combine(root,"changed-storage");Directory.CreateDirectory(otherPhysical);Directory.Delete(logical,false);DirectoryJunction.Create(logical,otherPhysical);Rejected(()=>mapping.Resolve(logical),"a system root retargeted during an operation is detected");Directory.Delete(logical,false);DirectoryJunction.Create(logical,physical);
        string missingTarget=Path.Combine(root,"missing-volume"),missingAlias=Path.Combine(root,"missing-LocalState");Directory.CreateDirectory(missingTarget);DirectoryJunction.Create(missingAlias,missingTarget);Directory.Move(missingTarget,missingTarget+"-detached");Rejected(()=>new BedrockSystemPath(missingAlias),"unavailable application storage volume is reported without creating a replacement directory");Directory.Delete(missingAlias,false);
        EncryptedParentCheck(root);CrossVolumeCheck(root);
        string? actual=Environment.GetEnvironmentVariable("BLOCKDOCK_TEST_SYSTEM_LOCALSTATE");if(!string.IsNullOrWhiteSpace(actual)){var source=new DirectoryInfo(actual).ResolveLinkTarget(true);var actualMap=new BedrockSystemPath(actual);Check(source!=null&&actualMap.Resolve(actual)==source.FullName&&actualMap.Resolve(Path.Combine(actual,"games"))==Path.Combine(source.FullName,"games"),"current Windows app volume relocation passes read-only resolution; no ACL, world or game package changed");}
        Directory.Delete(logical,false);Console.WriteLine("BEDROCK WINDOWS STORAGE CHECKS PASSED (workspace fixtures and optional read-only real path only)");
    }
    static void EncryptedParentCheck(string root)
    {
        string parent=Path.Combine(root,"encrypted-WpSystem-fixture"),target=Path.Combine(root,"encrypted-test-instance");Directory.CreateDirectory(parent);Directory.CreateDirectory(target);
        try{File.Encrypt(parent);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or PlatformNotSupportedException){Console.WriteLine("SKIP: optional EFS fixture unavailable: "+e.GetType().Name);return;}
        string alias=Path.Combine(parent,"data-link");DirectoryJunction.Create(alias,target);Check(new DirectoryInfo(alias).ResolveLinkTarget(true)!.FullName==target,"junction creation works under an EFS encrypted app-data parent");Directory.Delete(alias,false);File.Decrypt(parent);
    }
    static void CrossVolumeCheck(string root)
    {
        string tempRoot=Path.GetFullPath(Path.GetTempPath());if(string.Equals(Path.GetPathRoot(tempRoot),Path.GetPathRoot(root),StringComparison.OrdinalIgnoreCase)){Console.WriteLine("SKIP: no second volume available for actual cross-volume fixture");return;}
        string parent=Path.Combine(tempRoot,"BlockDock-storage-check-"+Guid.NewGuid().ToString("N"));if(!parent.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Temporary fixture must remain in system temp.");Directory.CreateDirectory(parent);
        string alias=Path.Combine(parent,"LocalState"),physical=Path.Combine(root,"cross-volume-appdata"),owned=Path.Combine(root,"cross-volume-instances"),live=Path.Combine(alias,"games","com.mojang");Directory.CreateDirectory(Path.Combine(physical,"games","com.mojang"));File.WriteAllText(Path.Combine(physical,"games","com.mojang","original"),"cross-volume-original");DirectoryJunction.Create(alias,physical);
        try{var link=new BedrockDataLink(live,owned,Path.Combine(root,"cross-volume.json"),systemPath:new BedrockSystemPath(alias));link.Activate(Path.Combine(owned,"one","user-data"));File.WriteAllText(Path.Combine(live,"instance-data"),"cross-volume-instance");Check(link.Restore()&&File.ReadAllText(Path.Combine(live,"original"))=="cross-volume-original"&&File.ReadAllText(Path.Combine(owned,"one","user-data","instance-data"))=="cross-volume-instance","actual system-temp and workspace volumes support isolated activation and verified restore");}
        finally{Directory.Delete(alias,false);Directory.Delete(parent,false);}
    }
}
