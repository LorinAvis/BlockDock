using System.Windows.Media.Imaging;

namespace BlockDock;

public static class DefaultWallpaper
{
    public static BitmapSource Image {get;}=Load();
    static BitmapSource Load()
    {
        var image=new BitmapImage();image.BeginInit();image.UriSource=new Uri("pack://application:,,,/BlockDock;component/Assets/DefaultWallpaper.png");image.CacheOption=BitmapCacheOption.OnLoad;image.EndInit();image.Freeze();return image;
    }
}
