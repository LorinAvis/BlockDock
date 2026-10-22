using System.Windows;
using System.Windows.Media;

namespace BlockDock;
// Original geometric badges; no Minecraft or other launcher's artwork is used.
public static class InstanceIcons
{
    static readonly Dictionary<string,DrawingImage> cache=new();
    public static ImageSource Get(string kind)
    {
        lock(cache){if(cache.TryGetValue(kind,out var saved))return saved;var group=new DrawingGroup();using(var dc=group.Open()){
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(236,242,249)),null,new Rect(0,0,42,42),8,8);
            void Shape(string points,Color color)=>dc.DrawGeometry(new SolidColorBrush(color),null,Geometry.Parse(points));
            if(kind is "mod" or "shader" or "resourcepack"){
                if(kind=="mod"){dc.DrawRoundedRectangle(Brushes.MediumSeaGreen,null,new Rect(9,9,25,25),3,3);dc.DrawEllipse(Brushes.MediumSeaGreen,null,new Point(21,8),5,5);dc.DrawEllipse(Brushes.MediumSeaGreen,null,new Point(9,22),5,5);dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(236,242,249)),null,new Point(34,21),4,4);}
                else if(kind=="shader"){dc.DrawEllipse(Brushes.Goldenrod,null,new Point(21,21),8,8);for(int n=0;n<8;n++){double a=n*Math.PI/4;dc.DrawLine(new Pen(Brushes.DarkGoldenrod,2),new(21+Math.Cos(a)*12,21+Math.Sin(a)*12),new(21+Math.Cos(a)*17,21+Math.Sin(a)*17));}}
                else{dc.DrawRectangle(Brushes.SteelBlue,null,new Rect(7,10,28,23));Shape("M 9,30 L 19,18 L 25,25 L 30,21 L 34,30 Z",Colors.LightGreen);}
            }else if(kind=="fabric"){
                dc.DrawRoundedRectangle(Brushes.Wheat,null,new Rect(12,9,18,24),3,3);dc.DrawRectangle(Brushes.SaddleBrown,null,new Rect(9,7,24,5));dc.DrawRectangle(Brushes.SaddleBrown,null,new Rect(9,31,24,5));for(int y=15;y<30;y+=4)dc.DrawLine(new Pen(Brushes.BurlyWood,2),new(13,y),new(29,y));
            }else if(kind is "forge" or "neoforge"){
                Shape("M 6,12 L 33,12 L 35,17 L 26,20 L 24,28 L 30,30 L 30,34 L 10,34 L 10,30 L 16,28 L 15,21 L 8,20 L 4,16 Z",kind=="neoforge"?Color.FromRgb(222,125,52):Color.FromRgb(84,103,127));dc.DrawRectangle(Brushes.LightSteelBlue,null,new Rect(9,10,23,4));
            }else{
                bool bedrock=kind=="bedrock";Shape("M 7,13 L 21,6 L 35,13 L 21,21 Z",bedrock?Color.FromRgb(139,151,165):Color.FromRgb(98,169,84));Shape("M 7,14 L 20,22 L 20,36 L 7,28 Z",bedrock?Color.FromRgb(80,94,112):Color.FromRgb(134,96,64));Shape("M 22,22 L 35,14 L 35,28 L 22,36 Z",bedrock?Color.FromRgb(110,122,139):Color.FromRgb(167,120,77));
            }
        }var image=new DrawingImage(group);image.Freeze();cache[kind]=image;return image;}
    }
}
