using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BlockDock;
public static class SkinAccounts
{
    static readonly HashSet<string> secrets=new();
    public static void RememberSecret(string value){if(value.Length>0)lock(secrets)secrets.Add(value);}
    public static string Redact(string text){lock(secrets)foreach(var secret in secrets.Where(x=>x.Length>6))text=text.Replace(secret,"[已隐藏]",StringComparison.Ordinal);return Regex.Replace(text,@"(?i)(access.?token|client.?token|refresh.?token|password|authorization)(\s*[:=]\s*)[^\s,;]+","$1$2[已隐藏]");}
    public static void ValidatePng(byte[] bytes)
    {
        if(bytes.Length<24||bytes.Length>4*1024*1024||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new IOException("皮肤需要不超过 4 MB 的 PNG 图片。");
        int width=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)),height=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
        if(width<64||width>1024||width%64!=0||(height!=width&&height!=width/2))throw new IOException("皮肤尺寸应为 64×64、64×32 或等比例高清尺寸。");
    }
    public static BitmapSource Texture(byte[] bytes)
    {ValidatePng(bytes);using var input=new MemoryStream(bytes);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=input;bitmap.EndInit();bitmap.Freeze();var result=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);result.Freeze();return result;}
    public static BitmapSource Face(BitmapSource texture,int x,int y,int width,int height,bool opaque=false,int scale=1,bool mirror=false)
    {
        int s=texture.PixelWidth/64,w=width*s,h=height*s;byte[] input=new byte[w*h*4];texture.CopyPixels(new Int32Rect(x*s,y*s,w,h),input,w*4,0);byte[] output=new byte[w*h*4*scale*scale];for(int py=0;py<h*scale;py++)for(int px=0;px<w*scale;px++){int source=((py/scale)*w+(mirror?w-1-px/scale:px/scale))*4,target=(py*w*scale+px)*4;for(int c=0;c<4;c++)output[target+c]=c==3&&opaque?(byte)255:input[source+c];}var image=BitmapSource.Create(w*scale,h*scale,96,96,PixelFormats.Bgra32,null,output,w*scale*4);image.Freeze();return image;
    }
    public static BitmapSource Avatar(byte[] bytes)
    {
        var texture=Texture(bytes);int size=texture.PixelWidth/8;var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);using(var dc=visual.RenderOpen()){dc.DrawImage(Face(texture,8,8,8,8,true),new Rect(0,0,size,size));dc.DrawImage(Face(texture,40,8,8,8),new Rect(0,0,size,size));}var result=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
    }
    public static BitmapSource RenderSkin(byte[] bytes,bool slim=false)
    {
        ValidatePng(bytes);var bitmap=new BitmapImage();using(var input=new MemoryStream(bytes)){bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=input;bitmap.EndInit();}bitmap.Freeze();
        int s=bitmap.PixelWidth/64;bool modern=bitmap.PixelHeight==bitmap.PixelWidth;var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            void Part(int sx,int sy,int w,int h,int x,int y){var crop=new CroppedBitmap(bitmap,new Int32Rect(sx*s,sy*s,w*s,h*s));RenderOptions.SetBitmapScalingMode(crop,BitmapScalingMode.NearestNeighbor);dc.DrawImage(crop,new Rect(x*6,y*6,w*6,h*6));}
            Part(8,8,8,8,8,0);Part(20,20,8,12,8,8);Part(44,20,slim?3:4,12,slim?5:4,8);Part(modern?36:44,modern?52:20,slim?3:4,12,16,8);
            Part(4,20,4,12,8,20);Part(modern?20:4,modern?52:20,4,12,12,20);
            Part(40,8,8,8,8,0);
            if(modern){Part(20,36,8,12,8,8);Part(44,36,slim?3:4,12,slim?5:4,8);Part(52,52,slim?3:4,12,16,8);Part(4,36,4,12,8,20);Part(4,52,4,12,12,20);}
        }
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);var target=new RenderTargetBitmap(144,192,96,96,PixelFormats.Pbgra32);target.Render(visual);target.Freeze();return target;
    }
}
