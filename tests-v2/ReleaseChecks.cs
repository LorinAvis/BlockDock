using BlockDock;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class ReleaseChecks
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool good,string text){if(!good)throw new Exception("FAIL: "+text);Console.WriteLine("PASS: "+text);}
    static T Field<T>(MainWindow window,string name)=>(T)typeof(MainWindow).GetField(name,Flags)!.GetValue(window)!;
    static void Call(MainWindow window,string name,params object[] args)=>typeof(MainWindow).GetMethod(name,Flags)!.Invoke(window,args);
    public static void Run()
    {
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var app=new BlockDock.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
        string folder=Path.GetFullPath("work/release-checks/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);new Settings{JavaRoot=Path.Combine(folder,"java"),SelectedServer="offline:obsolete-test-selection",WallpaperFile="",DownloadSourceIntroductionSeen=true}.Save();var window=new MainWindow();
        try
        {
            var pages=Field<Dictionary<string,FrameworkElement>>(window,"pages");var nav=Field<Dictionary<string,Button>>(window,"nav");Check(new[]{"friends","plugins","online"}.All(x=>!pages.ContainsKey(x)&&!nav.ContainsKey(x)),"removed features have no pages or sidebar navigation");
            var assembly=typeof(MainWindow).Assembly;Check(new[]{"OfflineAccount","OfflineAccounts","ExtensionCatalog","P2PSession","P2PEngine","EngineLifetime"}.All(x=>assembly.GetType("BlockDock."+x)==null)&&typeof(LaunchCore).GetMethods().All(x=>!x.Name.Contains("Offline")),"removed feature implementations and offline launch entry points are absent from assembly");
            Check(Field<ComboBox>(window,"accountBox").Items.Cast<object>().All(x=>x is string||x is MicrosoftAccount),"stale offline selection cannot create a usable offline identity");
            Check(new Settings().JavaRoot==Path.Combine(Settings.DataPath,"java",".minecraft"),"fresh game location is portable and independent of the author's machine");
            var wallpaper=Field<Image>(window,"wallpaperImage");Check(DefaultWallpaper.Image.PixelWidth==2560&&DefaultWallpaper.Image.PixelHeight==1088&&ReferenceEquals(wallpaper.Source,DefaultWallpaper.Image)&&wallpaper.Stretch==Stretch.UniformToFill,"default wallpaper is embedded at original resolution and fills the launcher without external paths");
            string custom=Path.Combine(folder,"自定义 背景.png");var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,256).ToArray(),32)));using(var stream=File.Create(custom))png.Save(stream);Call(window,"ApplyWallpaper",custom);Check(!ReferenceEquals(wallpaper.Source,DefaultWallpaper.Image),"custom local wallpaper still overrides the default");Call(window,"SetWallpaper","");Check(ReferenceEquals(wallpaper.Source,DefaultWallpaper.Image),"restore default returns to the embedded supplied illustration");Call(window,"ApplyWallpaper",Path.Combine(folder,"missing.png"));Check(ReferenceEquals(wallpaper.Source,DefaultWallpaper.Image),"missing custom file falls back to embedded wallpaper");
            foreach(string page in new[]{"home","account","settings","tasks"}){Call(window,"Navigate",page);Render(window,page,1460,930);Render(window,page,1120,780);}
            Console.WriteLine("RELEASE CHECKS PASSED; UI fixtures only, no real account or game changes");
        }
        finally{window.Close();SynchronizationContext.SetSynchronizationContext(context);}
    }
    static void Render(MainWindow window,string page,int width,int height)
    {
        var visual=(FrameworkElement)window.Content;visual.Measure(new Size(width,height));visual.Arrange(new Rect(0,0,width,height));visual.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));string root=Path.GetFullPath("artifacts/previews");Directory.CreateDirectory(root);using var stream=File.Create(Path.Combine(root,page+"-"+width+".png"));encoder.Save(stream);
    }
}
