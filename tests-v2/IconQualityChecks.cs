using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class IconQualityChecks
{
    static void Check(bool good,string label){if(!good)throw new Exception("FAIL: "+label);Console.WriteLine("PASS: "+label);}
    static byte[] Png(int size)
    {
        var pixels=new byte[size*size*4];for(int y=0;y<size;y++)for(int x=0;x<size;x++){int i=(y*size+x)*4;pixels[i+1]=(byte)((x+y)%2==0?255:0);pixels[i+2]=(byte)((x+y)%2==0?0:255);pixels[i+3]=255;}var image=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=new MemoryStream();encoder.Save(output);return output.ToArray();
    }
    static GameInstance Fixture(string root,string id,params int[] sizes)
    {
        string folder=Path.Combine(root,"versions",id);Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,id+".json"),"{\"type\":\"release\"}");using(var archive=ZipFile.Open(Path.Combine(folder,id+".jar"),ZipArchiveMode.Create))foreach(int size in sizes){using var stream=archive.CreateEntry("icons/icon_"+size+"x"+size+".png").Open();stream.Write(Png(size));}return new(id,id,"1.21.1","vanilla",folder,folder);
    }
    public static void Run()
    {
        string root=Path.GetFullPath("work/icon-quality-checks/"+Guid.NewGuid().ToString("N"));var high=Fixture(root,"high",32,128);var low=Fixture(root,"low",32);var highImage=(BitmapSource)high.Icon;var lowImage=(BitmapSource)low.Icon;Check(highImage.PixelWidth==128&&lowImage.PixelWidth==32,"vanilla selects largest original icon and never forces 32/128 pixel art through 64 pixels");
        var app=Application.Current;if(app==null){var created=new BlockDock.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};created.InitializeComponent();app=created;}
        var image=new Image{Source=lowImage,Width=96,Height=96};var host=new Window{Content=image,Width=96,Height=96};host.Measure(new Size(96,96));host.Arrange(new Rect(0,0,96,96));image.Measure(new Size(96,96));image.Arrange(new Rect(0,0,96,96));image.UpdateLayout();System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.DataBind);Check(RenderOptions.GetBitmapScalingMode(image)==BitmapScalingMode.NearestNeighbor,"actual application image style uses nearest-neighbor scaling for tiny pixel icons");var bitmap=new RenderTargetBitmap(96,96,96,96,PixelFormats.Pbgra32);bitmap.Render(image);var pixels=new byte[96*96*4];bitmap.CopyPixels(pixels,96*4,0);bool sharp=true;for(int i=0;i<pixels.Length;i+=4)if(pixels[i+3]!=255||pixels[i]!=0||!((pixels[i+1]==255&&pixels[i+2]==0)||(pixels[i+1]==0&&pixels[i+2]==255)))sharp=false;Check(sharp,"rendered 32-to-96 pixel icon has no blurred/interpolated colors");
        image.Source=highImage;image.UpdateLayout();System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.DataBind);Check(RenderOptions.GetBitmapScalingMode(image)==BitmapScalingMode.HighQuality,"larger modpack and wallpaper images retain high quality filtering");string output=Path.GetFullPath("artifacts/previews/原版图标-像素缩放验证.png");Directory.CreateDirectory(Path.GetDirectoryName(output)!);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(output))encoder.Save(stream);host.Close();Console.WriteLine("ICON QUALITY CHECKS PASSED");
    }
}
