using System.IO;

namespace BlockDock;

// Windows may place a package's LocalState on an app storage volume and leave a
// junction at its normal profile location. Resolve just this system boundary;
// instance roots and all descendants still use the ordinary no-links checks.
public sealed class BedrockSystemPath
{
    readonly string logicalRoot,physicalRoot;
    static bool Same(string a,string b)=>string.Equals(a.TrimEnd(Path.DirectorySeparatorChar),b.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
    public BedrockSystemPath(string localState)
    {
        logicalRoot=Path.GetFullPath(localState).TrimEnd(Path.DirectorySeparatorChar);
        physicalRoot=ReadRoot();
    }
    string ReadRoot()
    {
        BedrockDataLink.NoLinks(Path.GetDirectoryName(logicalRoot)!);
        var directory=new DirectoryInfo(logicalRoot);string target=logicalRoot;
        if(directory.LinkTarget!=null)
        {
            var resolved=directory.ResolveLinkTarget(true);
            if(resolved is not DirectoryInfo {Exists:true})throw new IOException("Windows 游戏数据所在的磁盘或目录不可用，请确认应用存储磁盘已连接："+logicalRoot);
            target=Path.GetFullPath(resolved.FullName);
        }
        if(target.StartsWith(@"\\")||target.Length<3||target[1]!=':')throw new IOException("基岩版系统数据需要位于 Windows 本地磁盘。");
        BedrockDataLink.NoLinks(target);return target;
    }
    public string Resolve(string path)
    {
        string full=Path.GetFullPath(path),relative=Path.GetRelativePath(logicalRoot,full);
        if(Path.IsPathRooted(relative)||relative==".."||relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new IOException("系统游戏数据路径超出 LocalState 目录。");
        if(!Same(physicalRoot,ReadRoot()))throw new IOException("Windows 游戏数据位置已发生变化，请重新启动此次操作；现有数据已保留。");
        return relative=="."?physicalRoot:Path.Combine(physicalRoot,relative);
    }
}
