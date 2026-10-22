using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlockDock;

public sealed record GameObservation(bool Exited,bool WindowReady,DateTimeOffset ProcessStarted,TimeSpan Elapsed,int? ExitCode,IntPtr Window);
public static class GameProcessMonitor
{
    // This is a real, visible process window milestone, not a timer or a claim
    // that a game's title screen/world has finished loading.
    public static async Task ObserveAsync(Process process,Action<GameObservation> report,CancellationToken ct)
    {
        DateTimeOffset started=new(process.StartTime.ToUniversalTime(),TimeSpan.Zero);bool ready=false;IntPtr window=IntPtr.Zero;
        while(true)
        {
            ct.ThrowIfCancellationRequested();process.Refresh();bool exited=process.HasExited;
            if(!exited){window=FindWindow(process.Id,process.MainWindowHandle);if(window!=IntPtr.Zero)ready=true;}
            DateTimeOffset end=exited?new DateTimeOffset(process.ExitTime.ToUniversalTime(),TimeSpan.Zero):DateTimeOffset.UtcNow;
            report(new(exited,ready,started,end-started,exited?process.ExitCode:null,window));if(exited)return;await Task.Delay(200,ct);
        }
    }
    public static IntPtr FindWindow(int pid,IntPtr preferred)
    {
        if(preferred!=IntPtr.Zero&&IsWindowVisible(preferred))return preferred;IntPtr found=IntPtr.Zero;
        EnumWindows((window,_)=>{if(!IsWindowVisible(window))return true;GetWindowThreadProcessId(window,out uint owner);if(owner==pid){found=window;return false;}bool childOwned=false;EnumChildWindows(window,(child,_)=>{GetWindowThreadProcessId(child,out uint childOwner);if(childOwner==pid&&IsWindowVisible(child)){childOwned=true;return false;}return true;},IntPtr.Zero);if(childOwned){found=window;return false;}return true;},IntPtr.Zero);return found;
    }
    public static void Focus(Process process){try{process.Refresh();var window=FindWindow(process.Id,process.MainWindowHandle);if(window!=IntPtr.Zero){ShowWindow(window,9);SetForegroundWindow(window);}}catch(InvalidOperationException){} }
    delegate bool WindowCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]static extern bool EnumWindows(WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr parent,WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
}
