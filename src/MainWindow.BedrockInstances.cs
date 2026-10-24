using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BlockDock;

public partial class MainWindow
{
    UIElement BedrockAutoSwitchControl(){var control=new CheckBox{Content="启动时自动切换到所选版本",IsChecked=settings.BedrockAutoSwitch,Foreground=Theme.Text};control.ToolTip="从底部或本页启动时，自动备份并切换 Windows 注册；每个版本继续使用自己的数据目录。";control.Checked+=(_,_)=>{settings.BedrockAutoSwitch=true;if(ready)settings.Save();};control.Unchecked+=(_,_)=>{settings.BedrockAutoSwitch=false;if(ready)settings.Save();};return control;}
    Task StartBedrockSelectionAsync(BedrockPackage package)=>RunLaunchAsync("基岩版 "+package.Version,"bedrock-system",(p,ct)=>BedrockLaunchFlow.StartAsync(package,settings.BedrockAutoSwitch,p,ct,LockLaunchDeployment),time=>settings.LastPlayed["bedrock:"+BedrockInstances.For(package).Family+":"+BedrockInstances.For(package).Version]=time);
    readonly TextBlock bedrockInstanceInfo=new(){TextWrapping=TextWrapping.Wrap,Foreground=Theme.Muted};
    void RefreshBedrockInstanceInfo()
    {
        if(packageList.SelectedItem is not BedrockPackage package){bedrockInstanceInfo.Text="下载或导入版本后，这里会显示独立实例。";return;}
        try{var instance=BedrockInstances.For(package);bedrockInstanceInfo.Text="独立数据目录："+instance.UserData+"\n游戏文件由 Windows 安装管理；存档、资源包和设置使用此实例的目录。";}
        catch(Exception e)when(e is IOException or System.Xml.XmlException or InvalidOperationException){bedrockInstanceInfo.Text="无法读取实例："+e.Message;}
    }
    void OpenBedrockData(BedrockPackage? selected=null)
    {
        if((selected??packageList.SelectedItem) is not BedrockPackage package)return;
        try{string folder=BedrockInstances.For(package).UserData;BedrockDataLink.NoLinks(folder);Directory.CreateDirectory(folder);Open(folder);}catch(Exception e){Error(e);}
    }
    async Task RestoreBedrockSystemAsync()
    {
        if(packageList.SelectedItem is not BedrockPackage package)return;
        try
        {
            var instance=BedrockInstances.For(package);BedrockInstances.RequireGameClosed();
            if(!Confirm("恢复此频道在 BlockDock 隔离前的系统数据目录？\n\n所有独立实例中的存档和内容会保留。请通过 BlockDock 启动以继续使用版本隔离。"))return;
            await RunAsync("恢复基岩版系统目录",async(p,ct)=>{bool restored=await Task.Run(()=>BedrockInstances.RestoreSystem(instance,ct),ct);Log(restored?"原系统数据目录已恢复，独立实例数据保留。":"尚未启用该频道的目录隔离，无需恢复。");},"bedrock-system",instance.Folder);
        }
        catch(Exception e){Error(e);}
    }
}
