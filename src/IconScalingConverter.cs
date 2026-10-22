using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;

public sealed class IconScalingConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>value is BitmapSource image&&Math.Max(image.PixelWidth,image.PixelHeight)<=64?BitmapScalingMode.NearestNeighbor:BitmapScalingMode.HighQuality;
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
