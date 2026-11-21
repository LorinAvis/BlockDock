using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class PackImportUiChecks
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(object window,string key)=>(T)window.GetType().GetField(key,Flags)!.GetValue(window)!;
    static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    public static void Run()
    {
        var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
        var old=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string root=Path.GetFullPath("work/pack-import-ui/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string package=Path.Combine(root,"我的整合包.zip");
        using(var zip=ZipFile.Open(package,ZipArchiveMode.Create))
        {
            Add(zip,"mcbbs.packmeta","""{"manifestType":"minecraftModpack","manifestVersion":2,"name":"林间漫游","version":"1.0","addons":[{"id":"game","version":"1.20.1"},{"id":"fabric","version":"0.16.9"}],"files":[]}""");
            Add(zip,"overrides/config/example.json","{}");Add(zip,"overrides/mods/example.jar","UI test data only");
        }
        var settings=new Settings{JavaRoot=Path.Combine(root,"java")};var window=new PackImportWindow(package,settings);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(Field<ProgressBar>(window,"progress").Visibility!=Visibility.Collapsed&&DateTime.UtcNow<deadline)
            {window.Dispatcher.Invoke(()=>{},DispatcherPriority.Background);Thread.Sleep(10);}
            Check(Field<Button>(window,"import").IsEnabled,"ZIP preview enables import after validated metadata is ready");
            var details=Field<TextBlock>(window,"details").Text;
            Check(details.Contains("1.20.1")&&details.Contains("fabric")&&details.Contains("0.16.9")&&details.Contains("2 个本地文件"),"preview shows actual game, loader and archive file count");
            Check(window.PackageSha256.Length==64&&window.InstanceName=="林间漫游","preview retains the file hash and editable pack name");
            Field<TextBox>(window,"name").Text="CON";Check(!Field<Button>(window,"import").IsEnabled,"invalid instance name cannot start an import");
            Field<TextBox>(window,"name").Text="林间漫游";Directory.CreateDirectory(Path.Combine(settings.JavaRoot,"versions","林间漫游"));
            Field<TextBox>(window,"name").Text="林间漫游 ";Check(Field<TextBlock>(window,"destination").Text.Contains("林间漫游 (2)"),"preview explains the new directory when an instance name already exists");
            var visual=(DockPanel)window.Content;visual.Background=Theme.Background;visual.Measure(new Size(650,570));visual.Arrange(new Rect(0,0,650,570));visual.UpdateLayout();
            var bitmap=new RenderTargetBitmap(650,570,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/previews");using var output=File.Create("artifacts/previews/pack-import.png");encoder.Save(output);
            Console.WriteLine("PACK IMPORT UI CHECKS PASSED; local fixtures only");
        }
        finally{window.Close();SynchronizationContext.SetSynchronizationContext(old);}
    }
    static void Add(ZipArchive zip,string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(content);}
}
