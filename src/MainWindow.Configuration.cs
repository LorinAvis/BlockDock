using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlockDock;
public partial class MainWindow
{
    readonly ComboBox javaMode=new(),javaDetected=new(),wallpaperFit=new();readonly TextBlock javaDetectionHint=new();readonly StackPanel javaCustom=new();
    readonly TextBox sourceName=new(),javaManifestUrl=new(),bedrockUwpUrl=new(),bedrockGdkUrl=new();readonly TextBlock providerHint=new(){TextWrapping=TextWrapping.Wrap};Border providerCard=null!;string pendingDownload="minecraft";
    UIElement BuildJavaConfiguration()
    {
        javaMode.ItemsSource=new[]{"自动选择 · 根据游戏版本匹配","自选 · 使用指定 Java"};javaMode.SelectedIndex=settings.JavaPath.Length==0?0:1;javaCustom.Children.Add(Text("检测到的 Java"));javaCustom.Children.Add(javaDetected);javaCustom.Children.Add(Text("程序位置"));javaCustom.Children.Add(javaBox);javaCustom.Children.Add(Button("浏览本地 Java",(_,_)=>{var d=new OpenFileDialog{Title="选择 java.exe 或 javaw.exe",Filter="Java 程序|java.exe;javaw.exe"};if(d.ShowDialog()==true){javaBox.Text=d.FileName;javaMode.SelectedIndex=1;}}));javaMode.SelectionChanged+=(_,_)=>javaCustom.Visibility=javaMode.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;javaCustom.Visibility=javaMode.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;javaDetected.ItemTemplate=Template("<StackPanel><TextBlock Text='{Binding Label}'/><TextBlock Text='{Binding Path}' FontSize='11' Foreground='#9CAEBB' TextTrimming='CharacterEllipsis'/></StackPanel>");javaDetected.SelectionChanged+=(_,_)=>{if(javaDetected.SelectedItem is JavaRuntime j&&javaMode.SelectedIndex==1)javaBox.Text=j.Path;};
        return V(Text("Java 运行环境",15,true),javaMode,Gap(8),javaDetectionHint,Button("重新检测 Java",async(_,_)=>await DetectJavaAsync()),Gap(6),javaCustom);
    }
    async Task DetectJavaAsync(){javaDetectionHint.Text="正在检测本机 Java…";try{var list=await Task.Run(JavaRuntimes.Detect);javaDetected.ItemsSource=list;javaDetected.SelectedItem=list.FirstOrDefault(x=>x.Path.Equals(javaBox.Text,StringComparison.OrdinalIgnoreCase));int required=8;if(homeGame.SelectedItem is GameInstance g){using var core=new LaunchCore(settings);required=core.RequiredJava(g);}var match=JavaRuntimes.Select(list,required);javaDetectionHint.Text=$"检测到 {list.Count} 个 Java。"+(match!=null?$"当前游戏自动选择 Java {match.Version}。":$"当前游戏需要 Java {required}，本机未找到匹配版本。");}catch(Exception e){javaDetectionHint.Text="检测未完成："+e.Message;}}
    UIElement BuildSourceConfiguration()
    {
        SourceConfig? saved=null;try{saved=DownloadProvider.Load();}catch(IOException){}sourceName.Text=saved?.Name??"";javaManifestUrl.Text=saved?.JavaManifestUrl??"";bedrockUwpUrl.Text=saved?.BedrockUwpUrl??"";bedrockGdkUrl.Text=saved?.BedrockGdkUrl??"";providerHint.Foreground=Theme.Muted;providerHint.Text=saved==null?"尚未配置游戏本体下载源。":"当前源："+saved.Name;
        providerCard=Card(V(Text("下载源",19,true),Hint("游戏本体下载源由你自行添加。模组等内容使用 Modrinth。"),Gap(),providerHint,Gap(),Text("来源名称（可选）"),sourceName,Gap(8),Text("Java 版本清单 URL"),javaManifestUrl,Gap(8),new Expander{Header="基岩版版本清单",Content=V(Text("UWP 清单 URL"),bedrockUwpUrl,Text("GDK 清单 URL"),bedrockGdkUrl)},Gap(),H(Button("保存下载源",(_,_)=>SaveUserSource(),true),Button("导入源配置",(_,_)=>ImportProvider()),Button("移除配置",(_,_)=>RemoveProvider())),Gap(8),Button("下载源格式说明",(_,_)=>Open("https://github.com/LorinAvis/BlockDock#下载源"))));return providerCard;
    }
    void SaveUserSource()
    {
        if(active!=null){providerHint.Text="请等当前任务结束后更换下载源。";return;}
        try{var old=DownloadProvider.Load();var config=new SourceConfig(1,sourceName.Text.Trim().Length>0?sourceName.Text.Trim():"我的下载源",old?.Rules??Array.Empty<SourceRule>(),javaManifestUrl.Text.Trim(),bedrockUwpUrl.Text.Trim(),bedrockGdkUrl.Text.Trim());config=DownloadProvider.Parse(System.Text.Json.JsonSerializer.Serialize(config));DownloadProvider.Save(config);settings.Source="Custom";settings.Save();versions.Clear();minecraftList.ItemsSource=null;bedrock.Clear();bedrockList.ItemsSource=null;providerHint.Text="下载源已保存："+config.Name;UpdateOverview();if((pendingDownload=="minecraft"&&DownloadProvider.HasJava)||(pendingDownload=="bedrock"&&DownloadProvider.HasBedrock))ShowDownload(pendingDownload);}catch(Exception e){providerHint.Text=e.Message;}
    }
    void OpenSourceSettings(string type="minecraft"){pendingDownload=type;settings.DownloadSourceIntroductionSeen=true;settings.Save();Navigate("settings");ShowSettingsSection("source");providerHint.Text="请添加自己的版本清单地址，再返回下载页面。";Dispatcher.BeginInvoke(()=>providerCard.BringIntoView());}
    bool EnsureSource(string type){bool available=type=="bedrock"?DownloadProvider.HasBedrock:DownloadProvider.HasJava;if(!available)OpenSourceSettings(type);else settings.Source="Custom";return available;}
}
