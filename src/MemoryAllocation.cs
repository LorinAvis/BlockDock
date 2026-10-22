using System.IO;
using System.Runtime.InteropServices;

namespace BlockDock;
public readonly record struct MemorySnapshot(long TotalMb,long AvailableMb);
public static class MemoryAllocation
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus { public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPageFile,AvailPageFile,TotalVirtual,AvailVirtual,AvailExtendedVirtual; }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static MemorySnapshot Snapshot(){var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref status))throw new IOException("无法读取系统内存，请改为手动设置。");return new((long)(status.TotalPhys/1048576),(long)(status.AvailPhys/1048576));}
    public static int Calculate(MemorySnapshot memory,bool modern,int mods,string mode,int manualMb,bool java32=false)
    {
        int limit=java32?1024:32768;if(mode=="Manual")return Math.Clamp(manualMb,512,limit);
        int desired=mods>=500?10240:mods>=350?8192:mods>=200?6144:mods>=100?5120:mods>=50?4096:mods>0?3072:modern?3072:2048;
        // Available memory is a changing warning signal, not the game's heap budget.
        // Size the heap for the instance while reserving at least half of physical RAM.
        long budget=Math.Max(512,memory.TotalMb/2);
        return (int)(Math.Clamp(Math.Min(desired,budget),512,limit)/256*256);
    }
    public static int Resolve(Settings settings,GameInstance? game,MemorySnapshot? snapshot=null,bool java32=false)
    {
        if(settings.MemoryMode=="Manual")return Calculate(default,false,0,"Manual",settings.MemoryMb,java32);
        int mods=0,major=8;try{if(game!=null){string folder=Path.Combine(game.GameDirectory,"mods");if(Directory.Exists(folder))mods=Directory.EnumerateFiles(folder,"*.jar").Count();major=LaunchCore.ReadMetadata(settings.JavaRoot,game.Id)["javaVersion"]?["majorVersion"]?.GetValue<int>()??8;}}catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Text.Json.JsonException){}catch(InvalidOperationException){}
        return Calculate(snapshot??Snapshot(),major>=17,mods,"Auto",settings.MemoryMb,java32);
    }
    public static string Format(int mb)=>$"{mb/1024.0:0.##} GB";
}
