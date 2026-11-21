using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlockDock;

public sealed class PackImportWindow : Window
{
    readonly TextBox name=new();
    readonly TextBlock details=new(){Text="正在识别整合包…",TextWrapping=TextWrapping.Wrap,LineHeight=24};
    readonly TextBlock destination=new(){Foreground=Theme.Muted,TextWrapping=TextWrapping.Wrap,LineHeight=21};
    readonly TextBlock message=new(){Foreground=Theme.Muted,TextWrapping=TextWrapping.Wrap,LineHeight=22};
    readonly Button import=new(){Content="导入到新实例",IsEnabled=false,MinWidth=145};
    readonly ProgressBar progress=new(){IsIndeterminate=true,Height=5,Margin=new(0,15,0,15)};
    readonly CancellationTokenSource cancellation=new();
    readonly string package,javaRoot;readonly Settings settings;bool closed,valid,inspecting;
    public string InstanceName=>name.Text.Trim();
    public string PackageSha256{get;private set;}="";
    public bool RequiresGameSource{get;private set;}=true;
    public PackImportWindow(string package,Settings settings)
    {
        this.package=Path.GetFullPath(package);this.settings=settings;javaRoot=Path.GetFullPath(settings.JavaRoot);
        Icon=BrandIcon.Image;Title="导入整合包 · BlockDock";Width=650;Height=570;MinWidth=570;MinHeight=510;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=Theme.Background;Foreground=Theme.Text;FontFamily=new("Microsoft YaHei UI");FontSize=13;
        var root=new DockPanel{Margin=new(26)};Content=root;
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,18,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var cancel=new Button{Content="取消",MinWidth=80,IsCancel=true};cancel.Click+=(_,_)=>Close();footer.Children.Add(cancel);footer.Children.Add(import);import.SetResourceReference(StyleProperty,"Primary");
        import.Click+=(_,_)=>{if(ValidateName())DialogResult=true;};
        var content=new StackPanel();root.Children.Add(new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        content.Children.Add(new TextBlock{Text="导入整合包",FontSize=25,FontWeight=FontWeights.SemiBold});
        content.Children.Add(new TextBlock{Text=Path.GetFileName(package),Foreground=Theme.Muted,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,18)});
        content.Children.Add(new Border{Child=details,Background=Theme.Surface,BorderBrush=Theme.Edge,BorderThickness=new(1),CornerRadius=new(10),Padding=new(16)});
        content.Children.Add(progress);content.Children.Add(new TextBlock{Text="实例名称",Margin=new(0,4,0,7)});content.Children.Add(name);
        content.Children.Add(destination);message.Margin=new(0,15,0,0);content.Children.Add(message);
        name.TextChanged+=(_,_)=>ValidateName();Loaded+=async(_,_)=>await InspectAsync();Closed+=(_,_)=>{closed=true;cancellation.Cancel();if(!inspecting)cancellation.Dispose();};
    }
    async Task InspectAsync()
    {
        inspecting=true;
        try
        {
            var info=await Task.Run(async()=>
            {
                using var pack=PackArchive.Open(package,cancellation.Token);
                await using var file=File.OpenRead(package);string hash=Convert.ToHexString(await SHA256.HashDataAsync(file,cancellation.Token));
                var dependencies=pack.Index["dependencies"]!.AsObject();var loader=dependencies.FirstOrDefault(x=>x.Key!="minecraft");
                return (Name:pack.Index["name"]?.ToString()??Path.GetFileNameWithoutExtension(package),pack.Format,Game:dependencies["minecraft"]!.ToString(),
                    Loader:loader.Key==null?"原版":loader.Key.Replace("-loader","")+" "+loader.Value,Count:pack.Entries.Count,Remote:pack.Index["files"]!.AsArray().Count,Size:pack.Entries.Sum(x=>x.Length),Hash:hash,Local:PackLocalVersions.Find(settings,pack.Index));
            },cancellation.Token);
            if(closed)return;
            details.Text=$"{info.Format}\nMinecraft {info.Game}  ·  {info.Loader}\n{info.Count:N0} 个本地文件 · {info.Size/1048576.0:N1} MB"+(info.Remote>0?$"\n另有 {info.Remote:N0} 个文件需要下载":"");
            PackageSha256=info.Hash;RequiresGameSource=info.Local==null;string suggested=info.Name;foreach(char c in Path.GetInvalidFileNameChars())suggested=suggested.Replace(c,'_');suggested=suggested.Trim().TrimEnd('.',' ');
            try{Paths.Id(suggested);}catch(IOException){suggested="我的整合包";}name.Text=suggested;valid=true;
            message.Text=info.Local!=null?"已找到匹配的本机游戏环境，导入后可在 Java 版中启动。":"自动安装对应游戏和加载器，再导入模组与配置。";
            message.Text+="\n相同整合包和名称会继续上次导入，保留已完成实例；其它同名实例会新建目录。进度可在任务中查看或取消。";
            if(RequiresGameSource&&!DownloadProvider.HasJava){message.Text+="\n请先配置 Java 下载源，保存后会继续本次导入。";import.Content="配置下载源并继续";}
            ValidateName();
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!closed){details.Text="暂时无法导入这个整合包";message.Foreground=Theme.Danger;message.Text=e is System.Text.Json.JsonException?"整合包清单不是有效的 JSON，请重新下载或重新导出。":e.Message;}}
        finally{inspecting=false;if(!closed){progress.Visibility=Visibility.Collapsed;name.IsEnabled=valid;}if(closed)cancellation.Dispose();}
    }
    bool ValidateName()
    {
        import.IsEnabled=false;if(!valid)return false;
        try
        {
            Paths.Id(InstanceName);string id=InstanceName;int count=2;
            while(Directory.Exists(Paths.Safe(javaRoot,"versions/"+id))||File.Exists(Paths.Safe(javaRoot,"versions/"+id)))id=InstanceName+" ("+(count++)+")";
            destination.Text="新建时保存到："+Paths.Safe(javaRoot,"versions/"+id);destination.Foreground=Theme.Muted;import.IsEnabled=true;return true;
        }
        catch(Exception e)when(e is IOException or ArgumentException){destination.Text=e.Message;destination.Foreground=Theme.Danger;return false;}
    }
}
