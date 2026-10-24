using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;
public static class Theme
{
    public static SolidColorBrush Brush(string color){var brush=(SolidColorBrush)new BrushConverter().ConvertFromString(color)!;brush.Freeze();return brush;}
    public static readonly Brush Background=Brush("#0B1117"),Surface=Brush("#121D27"),Raised=Brush("#182630"),Edge=Brush("#344854"),Accent=Brush("#20E494"),Text=Brush("#F1F6FA"),Muted=Brush("#A6BAC7");
    public static readonly Brush Focus=Brush("#9AF7D2"),Danger=Brush("#FF929A"),RaisedHover=Brush("#21343E");
    public static ImageSource? Image(string path){if(!File.Exists(path))return null;var value=new BitmapImage();value.BeginInit();value.UriSource=new Uri(Path.GetFullPath(path));value.CacheOption=BitmapCacheOption.OnLoad;value.EndInit();value.Freeze();return value;}
}
public sealed class CardWidthConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture){double width=value is double n&&n>100?n:800;double minimum=parameter?.ToString()=="instance"?470:310;int columns=Math.Max(1,(int)Math.Floor((width-12)/(minimum+12)));return Math.Max(240,(width-20)/columns-10);}
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
