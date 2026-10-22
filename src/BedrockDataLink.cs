using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlockDock;

// Only replaces a directory link recorded by this instance manager. Original
// data is copied and verified outside Windows package storage before redirecting.
public sealed class BedrockDataLink
{
    sealed record State(string Live,string Target,string Previous,string Backup,string Aside,bool HadOriginal);
    readonly string live,root,journal;readonly Action<string,string> createLink;readonly BedrockSystemPath? systemPath;
    public BedrockDataLink(string live,string ownedRoot,string journal,Action<string,string>? createLink=null,BedrockSystemPath? systemPath=null)
    {this.live=Path.GetFullPath(live).TrimEnd(Path.DirectorySeparatorChar);root=Path.GetFullPath(ownedRoot).TrimEnd(Path.DirectorySeparatorChar);this.journal=Path.GetFullPath(journal);this.createLink=createLink??DirectoryJunction.Create;this.systemPath=systemPath;}
    static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
    bool Inside(string path,string parent)=>Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    static bool Exists(string path)=>Directory.Exists(path)||File.Exists(path)||new DirectoryInfo(path).LinkTarget!=null;
    static string? Link(string path)=>new DirectoryInfo(path).LinkTarget is string value?Path.GetFullPath(value,Path.GetDirectoryName(path)!):null;
    internal static void NoLinks(string path)
    {
        for(var directory=new DirectoryInfo(path);directory!=null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("实例目录的父目录不能是链接："+directory.FullName);
    }
    void CheckSystemPath(string path)=>NoLinks(systemPath?.Resolve(path)??path);
    public void CheckTarget(string target)
    {
        if(!Inside(target,root)||Inside(target,Path.Combine(root,"_system-backups")))throw new IOException("存档目录必须位于基岩版实例目录中。");NoLinks(target);
    }
    State? Read()
    {
        if(!File.Exists(journal))return null;
        var state=JsonSerializer.Deserialize<State>(File.ReadAllText(journal))??throw new IOException("基岩版目录记录无法读取，请保留备份。");
        string asidePrefix=live+".blockdock-original-";
        if(state.Live==null||state.Target==null||state.Previous==null||state.Backup==null||state.Aside==null||!Same(state.Live,live)||!Inside(state.Target,root)||state.Previous.Length>0&&!Inside(state.Previous,root)||!Inside(state.Backup,Path.Combine(root,"_system-backups"))||!state.Aside.StartsWith(asidePrefix,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(state.Aside[asidePrefix.Length..],"N",out _))throw new IOException("基岩版目录记录不匹配，已停止修改。");
        NoLinks(root);NoLinks(state.Backup);CheckSystemPath(state.Aside);return state;
    }
    void Save(State state)=>Settings.AtomicText(journal,JsonSerializer.Serialize(state));
    void ArchiveJournal(string reason)=>File.Move(journal,journal+"."+reason+"-"+Guid.NewGuid().ToString("N"));
    void CheckOwned(string target,State? state)
    {
        if(state==null||!Inside(target,root)||!Same(target,state.Target)&&(state.Previous.Length==0||!Same(target,state.Previous)))throw new IOException("系统数据目录由其他程序链接，请先在原启动器恢复默认目录，BlockDock 不会接管该链接。");
    }
    public void Activate(string target,CancellationToken ct=default)
    {
        target=Path.GetFullPath(target);CheckTarget(target);CheckSystemPath(Path.GetDirectoryName(live)!);Directory.CreateDirectory(target);var state=Read();string? old=Link(live);if(old!=null){CheckOwned(old,state);if(Same(old,target))return;}
        // Check link privileges before moving any original files.
        string probe=Path.Combine(root,".link-check-"+Guid.NewGuid().ToString("N"));try{createLink(probe,target);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){throw new IOException("无法建立基岩版独立目录。请确认使用 Windows 本地磁盘且目录可写；原数据未移动。",e);}finally{if(Link(probe)!=null)Directory.Delete(probe,false);}
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);ct.ThrowIfCancellationRequested();
        if(state==null)
        {
            bool original=Directory.Exists(live);if(File.Exists(live))throw new IOException("系统数据目录被文件占用。");string id=Guid.NewGuid().ToString("N"),backup=Path.Combine(root,"_system-backups",id,"data"),aside=live+".blockdock-original-"+id;
            if(original){CopyTree(live,backup,ct);if(!EqualTree(live,backup,ct))throw new IOException("原基岩版数据备份校验失败，已停止隔离。");}
            state=new(live,target,"",backup,aside,original);Save(state);
            if(original)try{Directory.Move(live,aside);}catch{ArchiveJournal("not-activated");throw;}
        }
        else if(old==null&&Directory.Exists(live))
        {
            if(Directory.EnumerateFileSystemEntries(live).Any())throw new IOException("系统目录中出现了新的数据。请先点击“恢复系统目录”，保留这些数据后再启用隔离。");Directory.Delete(live,false);
        }
        var next=state with{Target=target,Previous=old??state.Target};Save(next);
        if(old!=null)Directory.Delete(live,false);
        try{createLink(live,target);Save(next with{Previous=""});}
        catch
        {
            if(Link(live)==null&&!Directory.Exists(live))
            {
                if(old!=null){DirectoryJunction.Create(live,old);Save(state);}
                else if(Directory.Exists(state.Aside)){Directory.Move(state.Aside,live);ArchiveJournal("rolled-back");}
            }
            // If the link exists but the final journal write failed, keep the
            // previously persisted pending state: it accepts both old and new.
            throw;
        }
    }
    public void DetachForDeployment()
    {
        var state=Read();if(state==null&&Link(live)==null)return;CheckSystemPath(Path.GetDirectoryName(live)!);string? target=Link(live);if(target==null)return;CheckOwned(target,state);Directory.Delete(live,false);Directory.CreateDirectory(live);
    }
    public bool Restore(CancellationToken ct=default)
    {
        var state=Read();if(state==null)return false;CheckSystemPath(Path.GetDirectoryName(live)!);string? target=Link(live);if(target!=null)CheckOwned(target,state);
        if(target==null&&Directory.Exists(live)&&Directory.EnumerateFileSystemEntries(live).Any())
        {
            if(state.HadOriginal&&Directory.Exists(state.Backup)&&EqualTree(live,state.Backup,ct)){ArchiveJournal("restored");return true;}
            throw new IOException("系统目录已有新数据，已保留两份数据。请先打开系统目录手动整理，不能自动覆盖。");
        }
        string? staged=null;
        if(state.HadOriginal&&!Directory.Exists(state.Aside))
        {
            if(!Directory.Exists(state.Backup))throw new IOException("原数据备份缺失，已停止恢复。");staged=live+".blockdock-restore-"+Guid.NewGuid().ToString("N");CopyTree(state.Backup,staged,ct);if(!EqualTree(state.Backup,staged,ct))throw new IOException("恢复校验失败，备份仍保留。");
        }
        ct.ThrowIfCancellationRequested();if(Exists(live))Directory.Delete(live,false);
        if(state.HadOriginal)Directory.Move(staged??state.Aside,live);
        ArchiveJournal("restored");return true;
    }
    static List<string> Files(string root)
    {
        var result=new List<string>();void Walk(string folder){if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("备份不跟随目录链接："+folder);foreach(var path in Directory.EnumerateFileSystemEntries(folder)){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("备份不跟随文件链接："+path);if(Directory.Exists(path))Walk(path);else result.Add(Path.GetRelativePath(root,path));}}Walk(root);return result;
    }
    static void CopyTree(string source,string destination,CancellationToken ct)
    {
        var files=Files(source);Directory.CreateDirectory(destination);void CopyDirectories(string folder){foreach(string child in Directory.EnumerateDirectories(folder)){ct.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,child)));CopyDirectories(child);}}CopyDirectories(source);foreach(string relative in files){ct.ThrowIfCancellationRequested();string output=Path.Combine(destination,relative);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(Path.Combine(source,relative),output,false);}
    }
    static bool EqualTree(string a,string b,CancellationToken ct)
    {
        var files=Files(a);if(files.Count!=Files(b).Count)return false;foreach(string relative in files){ct.ThrowIfCancellationRequested();string first=Path.Combine(a,relative),second=Path.Combine(b,relative);if(!File.Exists(second)||new FileInfo(first).Length!=new FileInfo(second).Length)return false;using var one=File.OpenRead(first);using var two=File.OpenRead(second);if(!SHA256.HashData(one).AsSpan().SequenceEqual(SHA256.HashData(two)))return false;}return true;
    }
}
