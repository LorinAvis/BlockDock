using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BlockDock;

// Directory junctions do not require the symbolic-link privilege. The reparse
// point itself is deleted with Directory.Delete(path, false), never recursively.
public static class DirectoryJunction
{
    public static void Create(string path,string target)
    {
        path=Path.GetFullPath(path);target=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if(target.StartsWith(@"\\")||target.Length<3||target[1]!=':')throw new IOException("基岩版独立目录需要 Windows 本地磁盘。");
        if(Directory.Exists(path)||File.Exists(path)||new DirectoryInfo(path).LinkTarget!=null)throw new IOException("目录链接位置已被占用。");
        byte[] substitute=Encoding.Unicode.GetBytes(@"\??\"+target),print=Encoding.Unicode.GetBytes(target);byte[] buffer=new byte[16+substitute.Length+2+print.Length+2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer,0);BitConverter.GetBytes(checked((ushort)(buffer.Length-8))).CopyTo(buffer,4);BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer,10);BitConverter.GetBytes(checked((ushort)(substitute.Length+2))).CopyTo(buffer,12);BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer,14);substitute.CopyTo(buffer,16);print.CopyTo(buffer,18+substitute.Length);
        Directory.CreateDirectory(path);
        try
        {
            using var handle=CreateFile(path,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(handle.IsInvalid||!DeviceIoControl(handle,0x000900A4,buffer,buffer.Length,IntPtr.Zero,0,out _,IntPtr.Zero))throw new IOException("无法创建基岩版目录联接。",new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch{Directory.Delete(path,false);throw;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputSize,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
}
