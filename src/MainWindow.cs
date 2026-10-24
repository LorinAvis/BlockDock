using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace BlockDock;
public partial class MainWindow : Window
{
    Settings settings=new();List<GameInstance> games=new();List<GameVersion> versions=new();List<BedrockVersion> bedrock=new();List<MicrosoftAccount> accounts=new();
    readonly ComboBox editionBox=new(),homeBedrock=new(),resourceSort=new(),installedGame=new(),installedKind=new();readonly StackPanel javaHome=new(),bedrockHome=new();
    readonly System.Collections.ObjectModel.ObservableCollection<LauncherJob> jobs=new();readonly ListView jobList=new(),homeJobs=new();readonly ResizableJobGate jobSlots=new();readonly Dictionary<string,SemaphoreSlim> jobScopes=new(StringComparer.OrdinalIgnoreCase);readonly AsyncLocal<LauncherJob?> jobContext=new();
    CancellationTokenSource? active,listCts,resourceCts,skinCts,loaderCts;bool ready,refreshing,bedrockOnline;string resourceType="minecraft",downloadView="minecraft",instanceFilter="all";int resourceOffset,totalResources;
    readonly Border workspaceFrame=new();readonly Grid pageHost=new(),downloadDeck=new();readonly Dictionary<string,FrameworkElement> pages=new();readonly Dictionary<string,Button> nav=new(),downloadTabs=new(),instanceTabs=new();
    readonly TextBlock title=new(){FontSize=25,FontWeight=FontWeights.Bold},subtitle=new(){Foreground=Theme.Muted,FontSize=12},status=new(){Text="准备就绪",FontSize=11,Foreground=Theme.Muted},headerAccount=new(),sourceStatus=new(),homeSummary=new(),selectedSummary=new(),installedStatus=new();
    readonly Image selectedCover=new(){Stretch=Stretch.UniformToFill},headerAvatar=new(){Width=32,Height=32};readonly StackPanel selectedFacts=new();
    readonly ComboBox homeGame=new(),accountBox=new(),targetBox=new(),loaderBox=new(),loaderVersion=new(),minecraftType=new(),resourceVersions=new(),bedrockType=new(),memoryBox=new(),sourceBox=new(),instanceSort=new();
    readonly TextBox gameSearch=new(),minecraftSearch=new(),instanceName=new(),resourceSearch=new(),bedrockSearch=new(),rootBox=new(),javaBox=new();
    readonly ListView gameList=new(),minecraftList=new(),resourceList=new(),bedrockList=new(),packageList=new(),installedList=new();
    readonly TextBlock gameInfo=new(),gameCount=new(),skinStatus=new(),loaderHint=new(),resourceStatus=new(),bedrockStatus=new(),taskTitle=new(),taskDetail=new(),resourceVersionHint=new();
    readonly Image skinImage=new(){Width=156,Height=208,Stretch=Stretch.Uniform};readonly TextBlock defaultSkin=new(){Text="未登录",FontSize=17,Foreground=Theme.Muted,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    readonly Button playButton=new(),installGameButton=new(),resourceInstallButton=new(),pauseButton=new();readonly ProgressBar progress=new(){Height=7,Maximum=100};
    readonly TextBox logBox=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Theme.Background,Foreground=Theme.Muted,FontSize=11,FontFamily=new("Consolas,Microsoft YaHei UI"),Padding=new(13),BorderThickness=new(0)};
    readonly StackPanel resourceTargetRow=new();readonly Grid minecraftPanel=new(),resourcesPanel=new(),bedrockButtons=new();
    static TextBlock Text(string value,double size=13,bool bold=false)=>new(){Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,LineHeight=size+9};
    static StackPanel V(params UIElement[] items){var p=new StackPanel();foreach(var item in items)p.Children.Add(item);return p;}
    static WrapPanel H(params UIElement[] items){var p=new WrapPanel();foreach(var item in items)p.Children.Add(item);return p;}
    static Border Card(UIElement child){var card=new Border{Child=child,CornerRadius=new(12),Padding=new(20),BorderBrush=Theme.Edge,BorderThickness=new(1)};card.SetResourceReference(BackgroundProperty,"CardBackground");return card;}
    static FrameworkElement Gap(double height=14)=>new Border{Height=height};
    static TextBlock Hint(string value)=>new(){Text=value,Foreground=Theme.Muted,TextWrapping=TextWrapping.Wrap,LineHeight=23};
    static void Row(Grid g,UIElement e,int row,int col=0){Grid.SetRow(e,row);Grid.SetColumn(e,col);g.Children.Add(e);}
    static Grid Rows(params GridLength[] values){var g=new Grid();foreach(var v in values)g.RowDefinitions.Add(new(){Height=v});return g;}
    static Grid Cols(params GridLength[] values){var g=new Grid();foreach(var v in values)g.ColumnDefinitions.Add(new(){Width=v});return g;}
    Button Button(string caption,RoutedEventHandler action,bool primary=false){var b=new Button{Content=caption,HorizontalContentAlignment=HorizontalAlignment.Center};if(primary)b.Style=(Style)FindResource("Primary");b.Click+=action;return b;}
    void Primary(Button b,string caption,RoutedEventHandler action){b.Content=caption;b.Style=(Style)FindResource("Primary");b.Click+=action;}
    static ScrollViewer Scroll(UIElement e)=>new(){Content=e,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    new static DataTemplate Template(string xaml)=>(DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"+xaml+"</DataTemplate>");
    static void Columns(ListView list,params(string Header,string Binding,double Width)[] columns){var view=new GridView();foreach(var col in columns){var c=new GridViewColumn{Header=col.Header,Width=col.Width};if(col.Binding=="Icon")c.CellTemplate=Template("<Image Source='{Binding Icon}' Width='30' Height='30'/>");else c.DisplayMemberBinding=new Binding(col.Binding);view.Columns.Add(c);}list.View=view;}
    void Page(string id,FrameworkElement element){pages[id]=element;element.Visibility=Visibility.Collapsed;if(id=="bedrock")downloadDeck.Children.Add(element);else pageHost.Children.Add(element);}
    void CardLayout(ListView list,string kind){list.ItemsPanel=(ItemsPanelTemplate)XamlReader.Parse("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><WrapPanel/></ItemsPanelTemplate>");ScrollViewer.SetCanContentScroll(list,false);var style=new Style(typeof(ListViewItem),(Style)FindResource(typeof(ListViewItem)));style.Setters.Add(new Setter(WidthProperty,new Binding("ActualWidth"){RelativeSource=new(RelativeSourceMode.FindAncestor,typeof(ListView),1),Converter=new CardWidthConverter(),ConverterParameter=kind}));list.ItemContainerStyle=style;}
    public MainWindow()
    {
        Icon=BrandIcon.Image;Title="BlockDock · "+LauncherVersion.Label;Width=1460;Height=930;MinWidth=1120;MinHeight=780;WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;Background=Theme.Background;Foreground=Theme.Text;FontFamily=new("Microsoft YaHei UI");FontSize=13;Closing+=Window_Closing;
        WindowChrome.SetWindowChrome(this,new(){CaptionHeight=0,ResizeBorderThickness=new(6),CornerRadius=new(12),GlassFrameThickness=new(0)});
        try{settings=Settings.Load();accounts=MicrosoftAccounts.Load();}catch(Exception e){MessageBox.Show(e.Message,"读取设置");}
        UseLayoutRounding=true;SnapsToDevicePixels=true;
        jobSlots.Limit=settings.ParallelTasks;var shell=Rows(new(58),new(1,GridUnitType.Star),new(108));shell.ColumnDefinitions.Add(new(){Width=new(208)});shell.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});var backdrop=new Grid{Background=Theme.Background};wallpaperLayer.Children.Add(wallpaperImage);wallpaperLayer.Children.Add(wallpaperVideo);backdrop.Children.Add(wallpaperLayer);backdrop.Children.Add(shell);Content=new Border{Child=backdrop,BorderBrush=Theme.Edge,BorderThickness=new(1),CornerRadius=new(12)};
        var side=Rows(GridLength.Auto,new(1,GridUnitType.Star),GridLength.Auto);side.Margin=new(12,22,12,16);var brand=H(new Image{Source=BrandIcon.Image,Width=38,Height=38,Margin=new(6,0,10,0)},V(Text("BlockDock",20,true),Text("MINECRAFT LAUNCHER",9)));Row(side,brand,0);
        var menu=BuildSidebarMenu();menu.Margin=new(0,22,0,0);Row(side,menu,1);var version=V(Text(LauncherVersion.Label,11),Text("你的方块世界",11));version.Margin=new(14,12,0,0);version.Opacity=.7;Row(side,new Border{BorderBrush=Theme.Edge,BorderThickness=new(0,1,0,0),Child=version},2);
        var sideFrame=new Border{Background=new LinearGradientBrush(Theme.Brush("#14222C").Color,Theme.Brush("#0D171F").Color,90),BorderBrush=Theme.Edge,BorderThickness=new(0,0,1,0),Child=side};Grid.SetRowSpan(sideFrame,3);sideFrame.SetResourceReference(BackgroundProperty,"ShellBackground");Row(shell,sideFrame,0);
        BuildTopBar(shell);
        var body=Rows(GridLength.Auto,new(1,GridUnitType.Star));body.Margin=new(22,4,22,16);var heroText=V(title,Gap(5),subtitle);var hero=new Border{Child=heroText,Padding=new(18,10,18,10),Margin=new(0,4,0,12),CornerRadius=new(12),Background=new LinearGradientBrush(Theme.Brush("#DA0D151D").Color,Theme.Brush("#850D151D").Color,0)};Row(body,hero,0);
        workspaceFrame.Child=pageHost;workspaceFrame.CornerRadius=new(16);workspaceFrame.ClipToBounds=true;workspaceFrame.SetResourceReference(BackgroundProperty,"WorkspaceBackground");Row(body,workspaceFrame,1);Row(shell,body,1,1);
        BuildLaunchBar(shell);BuildHome();BuildGames();BuildWorlds();BuildDownload();BuildBedrock();BuildInstalled();BuildTasks();BuildAccounts();BuildSettings();BuildAbout();
        ready=true;RefreshGames();ReloadAccounts();versions=DownloadProvider.HasJava?JavaService.CachedVersions():new();FilterMinecraft();ApplyWallpaper(settings.WallpaperFile);Navigate("home");_=DetectJavaAsync();
    }
}
