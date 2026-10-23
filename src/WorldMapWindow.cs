using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace BlockDock;

public sealed class WorldMapWindow : Window
{
    readonly SavedWorld? world;
    readonly StructureMapIcons icons;
    readonly ComboBox mode = new(), dimension = new(), range = new(), algorithm = new(), featureFilter = new();
    readonly TextBox seed = new(), x = new(), z = new();
    readonly CheckBox large = new() { Content = "大型生物群系", Foreground = Theme.Text, Margin = new(0,10,0,0) };
    readonly CheckBox gridLines = new() { Content = "坐标网格", Foreground = Theme.Muted, Margin = new(10,0,0,0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock message = new(), coordinate = new(), explanation = new(), scale = new(), structureHeading = new(), mapTitle = new();
    readonly Image image = new() { Width = 512, Height = 512, Stretch = Stretch.Fill };
    readonly Canvas canvas = new() { Width = 512, Height = 512, Background = Theme.Background };
    readonly ListView structures = new() { MaxHeight = 215 };
    readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Button render = new() { Content = "生成地图", Margin = new(0,12,0,0) }, cancel = new() { Content = "取消", Visibility = Visibility.Collapsed, Margin = new(0,6,0,0) };
    readonly Button localTab = new() { Content = "已探索地图" }, seedTab = new() { Content = "种子预测" };
    readonly Border loading = new() { Background = Theme.Surface, Padding = new(28), CornerRadius = new(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock loadingText = new() { Text = "准备地图", FontSize = 17, Foreground = Theme.Text, TextAlignment = TextAlignment.Center, MaxWidth = 300, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel legend = new();
    readonly List<FrameworkElement> controls = new();
    CancellationTokenSource? pending;
    WorldMapImage? raster; SeedMapResult? predicted; bool ready, fitting = true;
    double zoom = 1; Point? drag; double dragX, dragZ;

    public WorldMapWindow(SavedWorld? world = null)
    {
        this.world = world; icons=new(world); Title = "地图 · " + (world?.Name ?? "种子探索"); Width = 1260; Height = 870; MinWidth = 960; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Background; Foreground = Theme.Text; FontFamily = new("Microsoft YaHei UI"); Icon = BrandIcon.Image;
        var root = Rows(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto); root.Background = Theme.Background; Content = new Border { Background=Theme.Background, Padding=new(24), Child=root };
        mode.ItemsSource = world == null ? new[] { "种子预测" } : new[] { "已探索地图", "种子预测" }; mode.SelectedIndex = 0;
        dimension.ItemsSource = new[] { "主世界", "下界", "末地" }; dimension.SelectedIndex = 0;
        algorithm.ItemsSource = OfflineSeedMap.Algorithms; algorithm.SelectedItem = world == null ? OfflineSeedMap.Algorithms[15] : world.Source.Bedrock ? null : OfflineSeedMap.Match(world.Version.Length > 0 ? world.Version : world.Source.Version);
        seed.Text = world?.SeedText ?? "123456789";
        x.Text = (world?.SpawnX ?? 0).ToString(CultureInfo.InvariantCulture); z.Text = (world?.SpawnZ ?? 0).ToString(CultureInfo.InvariantCulture);
        localTab.Visibility = world == null ? Visibility.Collapsed : Visibility.Visible;
        localTab.Click += (_, _) => mode.SelectedIndex = 0; seedTab.Click += (_, _) => mode.SelectedIndex = world == null ? 0 : 1;
        var header = new DockPanel { Margin = new(0,0,0,20) };
        var tabs = Wrap(localTab, seedTab); DockPanel.SetDock(tabs,Dock.Right); header.Children.Add(tabs);
        header.Children.Add(Stack(Label(world?.Name ?? "种子探索",26,true), Muted(world?.Summary ?? "离线探索生物群系与结构",12))); Put(root,header,0);
        var side = Stack(Heading("世界与种子"), seed, Wrap(Small("复制种子",() => { try { Clipboard.SetText(seed.Text); message.Text = "种子已复制。"; } catch (Exception e) { message.Text = e.Message; } }),Small("出生点",LocateSpawn)), Heading("视图设置"), Label("维度",12),dimension,Label("显示范围 · 方块",12),range);
        var coords = new Grid(); coords.ColumnDefinitions.Add(new()); coords.ColumnDefinitions.Add(new(){Width=new(12)}); coords.ColumnDefinitions.Add(new());
        coords.Children.Add(Stack(Label("X",12),x)); var zz = Stack(Label("Z",12),z); Grid.SetColumn(zz,2); coords.Children.Add(zz); side.Children.Add(coords);
        side.Children.Add(algorithm); algorithm.Margin = new(0,12,0,0); side.Children.Add(large); side.Children.Add(render); side.Children.Add(cancel);
        render.SetResourceReference(StyleProperty,"Primary");
        var note = new Expander { Header="地图说明", Content=explanation, Margin=new(0,12,0,0) }; side.Children.Add(note);
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Foreground = Theme.Muted; explanation.FontSize=11;
        structureHeading.FontWeight=FontWeights.SemiBold; structureHeading.Margin=new(0,20,0,10); side.Children.Add(structureHeading); side.Children.Add(featureFilter); featureFilter.Margin=new(0,0,0,8);
        structures.ItemTemplate=(DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Foreground='#9CAEBB' FontSize='11' Margin='0,4,0,0'><Run Text='X '/><Run Text='{Binding X}'/><Run Text='   Z '/><Run Text='{Binding Z}'/></TextBlock></StackPanel></DataTemplate>");
        var itemDock=new FrameworkElementFactory(typeof(DockPanel));var itemIcon=new FrameworkElementFactory(typeof(Image));itemIcon.SetValue(WidthProperty,28d);itemIcon.SetValue(HeightProperty,28d);itemIcon.SetValue(MarginProperty,new Thickness(0,0,10,0));itemIcon.SetValue(RenderOptions.BitmapScalingModeProperty,BitmapScalingMode.NearestNeighbor);itemIcon.SetBinding(Image.SourceProperty,new Binding("Name"){Converter=new FeatureIconConverter(icons)});itemDock.AppendChild(itemIcon);
        var itemText=new FrameworkElementFactory(typeof(StackPanel));var itemName=new FrameworkElementFactory(typeof(TextBlock));itemName.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);itemName.SetBinding(TextBlock.TextProperty,new Binding("Name"));itemText.AppendChild(itemName);
        var itemCoords=new FrameworkElementFactory(typeof(TextBlock));itemCoords.SetValue(TextBlock.FontSizeProperty,11d);itemCoords.SetValue(TextBlock.ForegroundProperty,Theme.Muted);itemCoords.SetValue(MarginProperty,new Thickness(0,4,0,0));var coordsBinding=new MultiBinding{StringFormat="X {0}   Z {1}"};coordsBinding.Bindings.Add(new Binding("X"));coordsBinding.Bindings.Add(new Binding("Z"));itemCoords.SetBinding(TextBlock.TextProperty,coordsBinding);itemText.AppendChild(itemCoords);itemDock.AppendChild(itemText);structures.ItemTemplate=new DataTemplate{VisualTree=itemDock};
        structures.SelectionChanged += (_,_) => { if(structures.SelectedItem is SeedFeature f) { x.Text=f.X.ToString(); z.Text=f.Z.ToString(); CenterOn(f.X,f.Z); coordinate.Text=f.Name+$" · X {f.X} / Z {f.Z}"; DrawOverlay(); } };
        featureFilter.SelectionChanged += (_,_) => FilterFeatures(); side.Children.Add(structures);
        side.Children.Add(Heading("地图图例")); side.Children.Add(legend);
        var split = new Grid(); split.ColumnDefinitions.Add(new(){Width=new(258)}); split.ColumnDefinitions.Add(new(){Width=new(18)}); split.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        split.Children.Add(new Border { Child = Scroll(side), Background=Theme.Surface, BorderBrush=Theme.Edge, BorderThickness=new(1), Padding=new(16), CornerRadius=new(12) });
        var mapPanel=Rows(GridLength.Auto,new(1,GridUnitType.Star),GridLength.Auto); Grid.SetColumn(mapPanel,2); split.Children.Add(mapPanel); Put(root,split,1);
        var toolbar=new DockPanel {Margin=new(0,0,0,10)};
        var tools=Wrap(Small("−",()=>Zoom(zoom/1.3)),Small("＋",()=>Zoom(zoom*1.3)),Small("适合窗口",Fit),Small("导出 PNG",ExportImage)); DockPanel.SetDock(tools,Dock.Right);toolbar.Children.Add(tools);
        var labels=Stack(mapTitle,gridLines); mapTitle.FontSize=14;mapTitle.FontWeight=FontWeights.SemiBold; mapTitle.Margin=new(0,0,0,8); gridLines.Margin=new(0); toolbar.Children.Add(labels); Put(mapPanel,toolbar,0);
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor); canvas.Children.Add(image); viewport.Content=canvas;
        var stage=new Grid {Background=Theme.Brush("#101A22"),ClipToBounds=true};stage.Children.Add(viewport);
        loading.Child=Stack(new TextBlock{Text="◎",FontSize=36,Foreground=Theme.Accent,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,0,0,10)},loadingText); stage.Children.Add(loading);
        var compass=new Border {Child=Label("N ↑",12,true),Background=Theme.Brush("#DC101A22"),Padding=new(9,4,6,0),CornerRadius=new(6),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new(12),IsHitTestVisible=false};stage.Children.Add(compass);
        Put(mapPanel,new Border{Child=stage,BorderBrush=Theme.Edge,BorderThickness=new(1),CornerRadius=new(10),ClipToBounds=true},1);
        var foot=new DockPanel{Margin=new(2,12,0,0)}; scale.Foreground=Theme.Muted; scale.FontSize=11;DockPanel.SetDock(scale,Dock.Right);foot.Children.Add(scale); coordinate.Foreground=Theme.Text;coordinate.FontSize=12;foot.Children.Add(coordinate);Put(mapPanel,foot,2);
        message.TextWrapping=TextWrapping.Wrap;message.Foreground=Theme.Muted;message.FontSize=11;message.Margin=new(0,12,0,0);Put(root,message,2);
        controls.AddRange(new FrameworkElement[]{mode,localTab,seedTab,dimension,range,seed,algorithm,large,x,z});
        render.Click += async (_,_)=>await RenderAsync(); cancel.Click += (_,_)=>{pending?.Cancel();loadingText.Text="正在取消…";};
        mode.SelectionChanged += (_,_)=>{if(ready)ConfigureMode();};
        gridLines.Checked += (_,_)=>DrawOverlay();gridLines.Unchecked += (_,_)=>DrawOverlay();
        viewport.SizeChanged += (_,_)=>{if(fitting)Fit();};
        viewport.PreviewMouseWheel += (_,e)=>{Zoom(e.Delta>0?zoom*1.2:zoom/1.2);e.Handled=true;};
        canvas.MouseMove += (_,e)=>{if(raster==null)return;var point=e.GetPosition(canvas);int px=Math.Clamp((int)(point.X/512*raster.Width),0,raster.Width-1),pz=Math.Clamp((int)(point.Y/512*raster.Width),0,raster.Width-1);coordinate.Text=$"X {raster.MinX+px*raster.BlocksPerPixel}    Z {raster.MinZ+pz*raster.BlocksPerPixel}";if(predicted!=null)coordinate.Text+="    "+OfflineSeedMap.BiomeDisplayName(predicted.Algorithm,predicted.Biomes[pz*raster.Width+px]);};
        viewport.PreviewMouseLeftButtonDown += (_,e)=>{drag=e.GetPosition(viewport);dragX=viewport.HorizontalOffset;dragZ=viewport.VerticalOffset;viewport.CaptureMouse();};
        viewport.PreviewMouseMove += (_,e)=>{if(drag is {} start&&e.LeftButton==MouseButtonState.Pressed){var p=e.GetPosition(viewport);viewport.ScrollToHorizontalOffset(dragX+start.X-p.X);viewport.ScrollToVerticalOffset(dragZ+start.Y-p.Y);}};
        viewport.PreviewMouseLeftButtonUp += (_,_)=>{drag=null;viewport.ReleaseMouseCapture();};
        Closed += (_,_)=>pending?.Cancel();ready=true;ConfigureMode();Loaded += async (_,_)=>{if(render.IsEnabled)await RenderAsync();};
    }
    bool IsPrediction=>world==null||mode.SelectedIndex==1;
    internal bool ReadingWorld=>pending!=null&&!IsPrediction;
    void ConfigureMode()
    {
        pending?.Cancel();raster=null;predicted=null;image.Source=null;canvas.Children.Clear();canvas.Children.Add(image);structures.ItemsSource=null;featureFilter.ItemsSource=null;
        range.ItemsSource=IsPrediction?new[]{1024,4096,16384}:new[]{256,512,1024};range.SelectedIndex=1;
        bool supported=world?.Source.Bedrock!=true;
        algorithm.Visibility=large.Visibility=IsPrediction&&supported?Visibility.Visible:Visibility.Collapsed;
        structureHeading.Visibility=structures.Visibility=featureFilter.Visibility=IsPrediction&&supported?Visibility.Visible:Visibility.Collapsed;
        seed.IsReadOnly=!IsPrediction;render.IsEnabled=!IsPrediction||supported;
        localTab.Background=!IsPrediction?Theme.Brush("#154735"):Theme.Raised;seedTab.Background=IsPrediction?Theme.Brush("#154735"):Theme.Raised;
        localTab.Foreground=!IsPrediction?Theme.Accent:Theme.Text;seedTab.Foreground=IsPrediction?Theme.Accent:Theme.Text;
        mapTitle.Text=IsPrediction?"生物群系与结构":"已探索的世界";structureHeading.Text="结构位置";
        explanation.Text=IsPrediction? !supported?"基岩版离线预测尚未适配。可切换已探索地图查看本地世界。":"预测原版生物群系与部分结构，不包含玩家建筑。模组、数据包和跨版本区块可能不同；结构位置不保证实际生成。算法版本需与世界匹配。":"读取已保存的地表方块。灰格表示未探索或无法读取的区域。下界为顶部以下的切面；洞穴与较新的区块格式可能不支持。";
        loading.Visibility=Visibility.Visible;loadingText.Text=IsPrediction&&!supported?"基岩版预测尚未适配":"选择位置，开始探索";
        message.Text=IsPrediction&&!supported?explanation.Text:"拖动平移 · 滚轮缩放 · 修改坐标后点击生成地图";coordinate.Text="";scale.Text="";BuildLegend();
    }
    void LocateSpawn(){x.Text=(world?.SpawnX??0).ToString();z.Text=(world?.SpawnZ??0).ToString();CenterOn(world?.SpawnX??0,world?.SpawnZ??0);message.Text="已填入主世界出生点坐标；范围外请重新生成地图。";}
    void CenterOn(int mx,int mz){if(raster==null)return;viewport.ScrollToHorizontalOffset((mx-raster.MinX)/(double)(raster.Width*raster.BlocksPerPixel)*512*zoom-viewport.ActualWidth/2);viewport.ScrollToVerticalOffset((mz-raster.MinZ)/(double)(raster.Width*raster.BlocksPerPixel)*512*zoom-viewport.ActualHeight/2);}
    async Task RenderAsync()
    {
        if(pending!=null)return;
        if(!int.TryParse(x.Text,out int cx)||!int.TryParse(z.Text,out int cz)||range.SelectedItem is not int span){message.Text="请输入有效的 X、Z 坐标。";return;}
        bool prediction=IsPrediction;long value=0;var alg=algorithm.SelectedItem as SeedAlgorithm;
        if(prediction&&(!long.TryParse(seed.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out value)||alg==null)){message.Text="请输入整数种子，并选择已支持的 Java 算法版本。当前世界未适配时，请明确选择算法基准。";return;}
        var cts=pending=new();foreach(var control in controls)control.IsEnabled=false;render.IsEnabled=false;cancel.Visibility=Visibility.Visible;
        image.Source=null;raster=null;predicted=null;structures.ItemsSource=null;canvas.Children.Clear();canvas.Children.Add(image);loading.Visibility=Visibility.Visible;loadingText.Text=message.Text="正在读取地图…";
        int dim=dimension.SelectedIndex;bool big=large.IsChecked==true;
        try
        {
            var progress=new Progress<string>(s=>{message.Text=s;loadingText.Text=s;});
            if(prediction){await icons.LoadAsync(cts.Token);predicted=await Task.Run(()=>OfflineSeedMap.Render(world?.Source.Bedrock==true,alg!,value,cx,cz,span,dim,big,progress,cts.Token),cts.Token);raster=predicted.Image;}
            else raster=await Task.Run(()=>WorldMapRenderer.Render(world!,cx,cz,span,dim,progress,cts.Token),cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            int bitmapWidth=raster.DetailPixels!=null?raster.DetailWidth:raster.Width;var bitmap=BitmapSource.Create(bitmapWidth,bitmapWidth,96,96,PixelFormats.Bgra32,null,raster.DetailPixels??raster.Pixels,bitmapWidth*4);bitmap.Freeze();image.Source=bitmap;
            if(predicted!=null){featureFilter.ItemsSource=new[]{"全部结构"}.Concat(predicted.Features.Where(f=>f.Name!="古迹废墟").Select(f=>f.Name).Distinct().OrderBy(n=>n)).ToArray();featureFilter.SelectedIndex=0;message.Text=$"离线预测 · {predicted.Algorithm} · {predicted.Features.Count(f=>f.Name!="古迹废墟")} 个结构。预测结果可能与实际生成不同。";}
            else message.Text=raster.Chunks==0?"此范围没有可读取的已探索区块，请调整坐标或扩大范围。":$"已读取 {raster.Chunks} 个区块"+(raster.Unreadable>0?$" · {raster.Unreadable} 处未能读取，已留空。":" · 存档未修改。");
            if(!prediction)message.Text+=raster.TexturedBlocks>0?" 已启用方块贴图。":world?.Source.Bedrock==true?" 当前使用地表配色。":" 当前使用地表配色；此实例未找到可用的本机 Java 贴图。";
            loading.Visibility=Visibility.Collapsed;coordinate.Text=$"中心 X {cx}    Z {cz}";BuildLegend();Fit();DrawOverlay();
        }
        catch(OperationCanceledException){message.Text=loadingText.Text="地图生成已取消";}
        catch(Exception e){message.Text=loadingText.Text="地图生成未完成："+e.Message;}
        finally{pending=null;cts.Dispose();foreach(var control in controls)control.IsEnabled=true;render.IsEnabled=!IsPrediction||world?.Source.Bedrock!=true;cancel.Visibility=Visibility.Collapsed;}
    }
    IEnumerable<SeedFeature> VisibleFeatures=>predicted?.Features.Where(f=>f.Name!="古迹废墟"&&(featureFilter.SelectedItem is not string filter||filter=="全部结构"||f.Name==filter))??Enumerable.Empty<SeedFeature>();
    void FilterFeatures(){structures.ItemsSource=VisibleFeatures.ToArray();structureHeading.Text=$"结构位置 · {VisibleFeatures.Count()}";DrawOverlay();}
    void DrawOverlay()
    {
        canvas.Children.Clear();canvas.Children.Add(image);if(raster==null)return;
        if(gridLines.IsChecked==true)for(int n=1;n<8;n++)
        {
            double p=n*64;canvas.Children.Add(new Line{X1=p,X2=p,Y1=0,Y2=512,Stroke=Theme.Brush("#507C929A"),StrokeThickness=.65/zoom,IsHitTestVisible=false});canvas.Children.Add(new Line{X1=0,X2=512,Y1=p,Y2=p,Stroke=Theme.Brush("#507C929A"),StrokeThickness=.65/zoom,IsHitTestVisible=false});
            var label=new TextBlock{Text=(raster.MinX+n*raster.Width*raster.BlocksPerPixel/8).ToString(),FontSize=9/zoom,Foreground=Brushes.White,Background=Theme.Brush("#99101A22"),IsHitTestVisible=false};Canvas.SetLeft(label,p+2);Canvas.SetTop(label,2);canvas.Children.Add(label);
        }
        // Suppress collisions at the current zoom. Filtering or zooming reveals nearby markers.
        var occupied=new List<Point>();
        foreach(var f in VisibleFeatures.OrderBy(f=>f==structures.SelectedItem as SeedFeature?0:1))
        {
            double px=(f.X-raster.MinX)/(double)(raster.Width*raster.BlocksPerPixel)*512,pz=(f.Z-raster.MinZ)/(double)(raster.Width*raster.BlocksPerPixel)*512;
            var position=new Point(px*zoom,pz*zoom);if(occupied.Any(p=>Math.Abs(p.X-position.X)<24&&Math.Abs(p.Y-position.Y)<24))continue;occupied.Add(position);
            string glyph=f.Name switch{"村庄"=>"村","埋藏的宝藏"=>"宝","废弃传送门"=>"门","海底神殿"=>"海","远古城市"=>"城","林地府邸"=>"邸","试炼密室"=>"试",_=>f.Name[..1]};
            var color=f.Name.Contains("海")?"#9EDAE4":f.Name.Contains("传送")?"#C7ADF0":"#F2D3A2";
            AddMarker(f.X,f.Z,glyph,Theme.Brush(color),f.Name,f);
        }
        if(world!=null&&predicted==null&&dimension.SelectedIndex==0)AddMarker(world.SpawnX,world.SpawnZ,"⌂",Theme.Accent,"出生点",null);
        scale.Text=$"{raster.Width*raster.BlocksPerPixel:N0} × {raster.Width*raster.BlocksPerPixel:N0} 方块    ·    {zoom*100:0}%";
    }
    void AddMarker(int mx,int mz,string glyph,Brush color,string name,SeedFeature? feature)
    {
        if(raster==null)return;double px=(mx-raster.MinX)/(double)(raster.Width*raster.BlocksPerPixel)*512,pz=(mz-raster.MinZ)/(double)(raster.Width*raster.BlocksPerPixel)*512;
        if(px<0||px>=512||pz<0||pz>=512)return;
        double size=23/zoom;var marker=new Border{Width=size,Height=size,CornerRadius=new(6/zoom),Background=Theme.Brush("#ED14232D"),BorderBrush=color,BorderThickness=new((feature==structures.SelectedItem as SeedFeature?2:1)/zoom),ToolTip=name+$" · X {mx} / Z {mz}",Child=new TextBlock{Text=glyph,FontSize=12/zoom,FontWeight=FontWeights.Bold,Foreground=color,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
        if(icons.Get(name) is {} icon){var img=new Image{Source=icon,Margin=new(1/zoom)};RenderOptions.SetBitmapScalingMode(img,BitmapScalingMode.NearestNeighbor);marker.Child=img;}
        Canvas.SetLeft(marker,px-size/2);Canvas.SetTop(marker,pz-size/2);canvas.Children.Add(marker);
    }
    void BuildLegend()
    {
        legend.Children.Clear();
        if(predicted!=null)
        {
            foreach(var group in predicted.Biomes.GroupBy(b=>b).OrderByDescending(g=>g.Count()).Take(8))
            {
                int rgb=OfflineSeedMap.BiomeColor(predicted.Algorithm,group.Key);var row=Wrap(new Border{Width=10,Height=10,Background=new SolidColorBrush(Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb)),CornerRadius=new(2),Margin=new(0,0,8,0),VerticalAlignment=VerticalAlignment.Center},Muted(OfflineSeedMap.BiomeDisplayName(predicted.Algorithm,group.Key),11));row.Margin=new(0,0,0,6);legend.Children.Add(row);
            }
            return;
        }
        var entries=IsPrediction?new[]{("海洋 / 河流","#588995"),("平原 / 森林","#7E9A67"),("沙漠 / 荒地","#C6AF76"),("雪地 / 高山","#C7D6D6"),("结构 · 悬停看坐标","#F2D3A2")}:new[]{("植被","#6B9758"),("水域","#426E9A"),("建筑 / 地表方块","#B4936A"),("未探索 / 无法读取","#35434B")};
        foreach(var (text,color) in entries){var row=Wrap(new Border{Width=9,Height=9,Background=Theme.Brush(color),CornerRadius=new(2),Margin=new(0,0,8,0),VerticalAlignment=VerticalAlignment.Center},Muted(text,11));row.Margin=new(0,0,0,4);legend.Children.Add(row);}
    }
    void Zoom(double value){fitting=false;double old=zoom;zoom=Math.Clamp(value,.4,8);canvas.LayoutTransform=new ScaleTransform(zoom,zoom);viewport.ScrollToHorizontalOffset((viewport.HorizontalOffset+viewport.ActualWidth/2)*zoom/old-viewport.ActualWidth/2);viewport.ScrollToVerticalOffset((viewport.VerticalOffset+viewport.ActualHeight/2)*zoom/old-viewport.ActualHeight/2);DrawOverlay();}
    void Fit(){Zoom(viewport.ActualWidth>0&&viewport.ActualHeight>0?Math.Min((viewport.ActualWidth-4)/512,(viewport.ActualHeight-4)/512):1);fitting=true;}
    void ExportImage()
    {
        if(image.Source is not BitmapSource){message.Text="请先生成地图。";return;}
        var dialog=new SaveFileDialog{Title="导出地图图片",Filter="PNG 图片|*.png",FileName="BlockDock-地图.png"};if(dialog.ShowDialog(this)!=true)return;
        string? temporary=null;
        try
        {
            string output=System.IO.Path.GetFullPath(dialog.FileName);WorldFiles.NoLinks(output);
            if(world!=null&&output.StartsWith(System.IO.Path.GetFullPath(world.Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("请将地图图片保存到世界目录之外。");
            int size=Math.Max(1024,raster?.DetailWidth??0);var bitmap=new RenderTargetBitmap(size,size,size/512d*96,size/512d*96,PixelFormats.Pbgra32);
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(new VisualBrush(canvas){Stretch=Stretch.Fill},null,new Rect(0,0,512,512));}bitmap.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            temporary=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!,".blockdock-map-"+Guid.NewGuid().ToString("N")+".tmp");
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){encoder.Save(file);}
            WorldFiles.NoLinks(output);File.Move(temporary,output,true);temporary=null;
            message.Text="地图已导出，包含当前标记与网格。";
        }
        catch(Exception e){message.Text="导出未完成："+e.Message;}
        finally{if(temporary!=null)try{File.Delete(temporary);}catch(IOException){}}
    }
    static Button Small(string text,Action action){var b=new Button{Content=text,Padding=new(10,7,10,7),FontSize=12,Margin=new(0,0,6,0)};b.Click+=(_,_)=>action();return b;}
    static TextBlock Label(string text,double size=13,bool bold=false)=>new(){Text=text,FontSize=size,Foreground=Theme.Text,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,6)};
    static TextBlock Muted(string text,double size=12){var t=Label(text,size);t.Foreground=Theme.Muted;t.Margin=new(0);return t;}
    static TextBlock Heading(string text){var t=Label(text,13,true);t.Margin=new(0,16,0,10);return t;}
    static StackPanel Stack(params UIElement[] items){var p=new StackPanel();foreach(var item in items)p.Children.Add(item);return p;}
    static WrapPanel Wrap(params UIElement[] items){var p=new WrapPanel{Margin=new(0,8,0,0)};foreach(var item in items)p.Children.Add(item);return p;}
    static ScrollViewer Scroll(UIElement item)=>new(){Content=item,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    static Grid Rows(params GridLength[] heights){var g=new Grid();foreach(var height in heights)g.RowDefinitions.Add(new(){Height=height});return g;}
    static void Put(Grid g,UIElement item,int row){Grid.SetRow(item,row);g.Children.Add(item);}
    sealed class FeatureIconConverter(StructureMapIcons icons):IValueConverter
    {
        public object? Convert(object value,Type type,object parameter,CultureInfo culture)=>icons.Get(value?.ToString()??"");
        public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
    }
}
