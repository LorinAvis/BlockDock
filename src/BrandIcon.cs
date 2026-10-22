using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;
public static class BrandIcon
{
    static BitmapImage? image;
    public static ImageSource Image {get{if(image==null){image=new BitmapImage(new Uri("pack://application:,,,/BlockDock;component/Assets/BlockDock.png",UriKind.Absolute));image.Freeze();}return image;}}
}
