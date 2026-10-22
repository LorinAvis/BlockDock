using System.Diagnostics;
using System.IO;

namespace BlockDock;

public interface IBedrockPlatform
{
    Task<WindowsGame?> CurrentAsync(BedrockPackage package,CancellationToken ct);
    Task InstallAsync(BedrockPackage package,IProgress<WorkProgress> progress,CancellationToken ct,Action deploymentStarting);
    Task<Process> ActivateAsync(BedrockPackage package,IProgress<WorkProgress> progress,CancellationToken ct);
}
public static class BedrockLaunchFlow
{
    sealed class NativePlatform:IBedrockPlatform
    {
        public Task<WindowsGame?> CurrentAsync(BedrockPackage package,CancellationToken ct)=>BedrockNative.CurrentForAsync(package,ct);
        public Task InstallAsync(BedrockPackage package,IProgress<WorkProgress> progress,CancellationToken ct,Action locked)=>BedrockNative.InstallAsync(package,true,progress,ct,locked);
        public Task<Process> ActivateAsync(BedrockPackage package,IProgress<WorkProgress> progress,CancellationToken ct)=>BedrockNative.LaunchAsync(package,ct,progress);
    }
    public static bool NeedsSwitch(BedrockPackage package,WindowsGame? installed)
    {
        var instance=BedrockInstances.For(package);return installed==null||installed.Name!=instance.Name||installed.Family!=instance.Family||installed.Version!=instance.Version||installed.IsDevelopmentMode;
    }
    public static async Task<Process> StartAsync(BedrockPackage package,bool autoSwitch,IProgress<WorkProgress> progress,CancellationToken ct,Action deploymentStarting,IBedrockPlatform? platform=null)
    {
        platform??=new NativePlatform();progress.Report(new("检查所选基岩版与系统版本…"));var current=await platform.CurrentAsync(package,ct);
        if(NeedsSwitch(package,current)){if(!autoSwitch)throw new IOException("当前 Windows 版本与所选实例不同。请先安装 / 切换，或开启“启动时自动切换版本”。");progress.Report(new("正在切换到基岩版 "+package.Version+"，原世界与实例数据会保留…"));await platform.InstallAsync(package,progress,ct,deploymentStarting);}
        ct.ThrowIfCancellationRequested();return await platform.ActivateAsync(package,progress,ct);
    }
}
