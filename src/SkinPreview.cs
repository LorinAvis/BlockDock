using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace BlockDock;

public sealed class SkinPreview : Grid
{
    readonly Viewport3D viewport=new(){Width=300,HorizontalAlignment=HorizontalAlignment.Center};readonly Model3DGroup player=new();readonly AxisAngleRotation3D yaw=new(new Vector3D(0,1,0),-20),pitch=new(new Vector3D(1,0,0),0);readonly PerspectiveCamera camera=new(new Point3D(0,16,55),new Vector3D(0,0,-55),new Vector3D(0,1,0),38);
    BitmapSource? texture;Point pointer;bool dragging,slim,layers=true;
    public double Yaw=>yaw.Angle;public double Pitch=>pitch.Angle;public double Distance=>camera.Position.Z;
    public bool Slim{get=>slim;set{slim=value;Rebuild();}}
    public bool Layers{get=>layers;set{layers=value;Rebuild();}}
    public SkinPreview()
    {
        Height=300;Background=Brushes.Transparent;ClipToBounds=true;Cursor=Cursors.SizeAll;viewport.Camera=camera;var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Colors.White));scene.Children.Add(player);viewport.Children.Add(new ModelVisual3D{Content=scene});Children.Add(viewport);var transforms=new Transform3DGroup();transforms.Children.Add(new RotateTransform3D(yaw,0,16,0));transforms.Children.Add(new RotateTransform3D(pitch,0,16,0));player.Transform=transforms;
        MouseLeftButtonDown+=(_,e)=>{pointer=e.GetPosition(this);dragging=CaptureMouse();e.Handled=true;};MouseMove+=(_,e)=>{if(!dragging)return;var current=e.GetPosition(this);Rotate(current.X-pointer.X,current.Y-pointer.Y);pointer=current;};MouseLeftButtonUp+=(_,e)=>{dragging=false;ReleaseMouseCapture();e.Handled=true;};LostMouseCapture+=(_,_)=>dragging=false;MouseWheel+=(_,e)=>{Zoom(e.Delta>0?-4:4);e.Handled=true;};
    }
    public void SetSkin(byte[]? bytes,bool isSlim=false){var decoded=bytes==null?null:SkinAccounts.Texture(bytes);texture=decoded;slim=isSlim;Rebuild();}
    public void Rotate(double x,double y){yaw.Angle=(yaw.Angle+x*.65)%360;pitch.Angle=Math.Clamp(pitch.Angle+y*.5,-45,45);}
    public void View(double angle){yaw.Angle=angle;pitch.Angle=0;}
    public void Zoom(double delta){double z=Math.Clamp(camera.Position.Z+delta,42,100);camera.Position=new(0,16,z);camera.LookDirection=new(0,0,-z);}
    public void ResetView(){View(-20);camera.Position=new(0,16,55);camera.LookDirection=new(0,0,-55);}
    void Rebuild()
    {
        player.Children.Clear();if(texture==null)return;bool modern=texture.PixelHeight==texture.PixelWidth;int arm=slim?3:4;
        void Box(double x,double y,double z,int w,int h,int d,int u,int v,bool overlay=false,bool mirror=false)
        {
            double expand=overlay?.22:0,x0=x-expand,x1=x+w+expand,y0=y-expand,y1=y+h+expand,z0=z-expand,z1=z+d+expand;int up=Math.Max(1,16/(texture.PixelWidth/64));
            void Face(Point3D a,Point3D b,Point3D c,Point3D e,int sx,int sy,int sw,int sh)
            {
                var mesh=new MeshGeometry3D{Positions=new(new[]{a,b,c,e}),TriangleIndices=new(new[]{0,1,2,0,2,3}),TextureCoordinates=new(new[]{new Point(0,0),new Point(0,1),new Point(1,1),new Point(1,0)})};mesh.Freeze();var bitmap=SkinAccounts.Face(texture,sx,sy,sw,sh,!overlay,up,mirror);var brush=new ImageBrush(bitmap){Stretch=Stretch.Fill,TileMode=TileMode.None};RenderOptions.SetBitmapScalingMode(brush,BitmapScalingMode.NearestNeighbor);brush.Freeze();var material=new DiffuseMaterial(brush);material.Freeze();var face=new GeometryModel3D(mesh,material);face.Freeze();player.Children.Add(face);
            }
            // Quad order gives outward normals and top-to-bottom texture rows.
            Face(new(x0,y1,z1),new(x0,y0,z1),new(x1,y0,z1),new(x1,y1,z1),u+d,v+d,w,h);
            Face(new(x1,y1,z0),new(x1,y0,z0),new(x0,y0,z0),new(x0,y1,z0),u+d+w+d,v+d,w,h);
            Face(new(x1,y1,z1),new(x1,y0,z1),new(x1,y0,z0),new(x1,y1,z0),mirror?u:u+d+w,v+d,d,h);
            Face(new(x0,y1,z0),new(x0,y0,z0),new(x0,y0,z1),new(x0,y1,z1),mirror?u+d+w:u,v+d,d,h);
            Face(new(x0,y1,z0),new(x0,y1,z1),new(x1,y1,z1),new(x1,y1,z0),u+d,v,w,d);
            Face(new(x0,y0,z1),new(x0,y0,z0),new(x1,y0,z0),new(x1,y0,z1),u+d+w,v,w,d);
        }
        Box(-4,24,-4,8,8,8,0,0);Box(-4,12,-2,8,12,4,16,16);Box(-4-arm,12,-2,arm,12,4,40,16);Box(4,12,-2,arm,12,4,modern?32:40,modern?48:16,mirror:!modern);Box(-4,0,-2,4,12,4,0,16);Box(0,0,-2,4,12,4,modern?16:0,modern?48:16,mirror:!modern);
        if(!layers)return;Box(-4,24,-4,8,8,8,32,0,true);if(modern){Box(-4,12,-2,8,12,4,16,32,true);Box(-4-arm,12,-2,arm,12,4,40,32,true);Box(4,12,-2,arm,12,4,48,48,true);Box(-4,0,-2,4,12,4,0,32,true);Box(0,0,-2,4,12,4,0,48,true);}
    }
}
