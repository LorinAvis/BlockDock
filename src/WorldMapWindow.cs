using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BlockDock;

public sealed class WorldMapWindow : Window
{
    readonly SavedWorld? world;
    readonly StructureMapIcons icons;
    readonly ComboBox mode = new(), dimension = new(), range = new(), algorithm = new(), featureFilter = new(), featureSort = new();
    readonly TextBox seed = new(), x = new(), z = new(), featureSearch = new();
    readonly CheckBox large = new() { Content = "大型生物群系", Foreground = Theme.Muted, Margin = new(0, 10, 0, 4) };
    readonly CheckBox gridLines = new() { Content = "网格", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    readonly CheckBox showMarkers = new() { Content = "标记", IsChecked = true, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    readonly TextBlock message = new(), coordinate = new(), explanation = new(), scale = new(), structureHeading = new(), structureEmpty = new(), mapTitle = new(), dirtyHint = new();
    readonly TextBlock selectedName = new(), selectedCoordinates = new(), selectionNote = new(), selectedContext = new();
    readonly Image selectionIcon = new() { Width = 32, Height = 32, Stretch = Stretch.Uniform };
    readonly Image image = new() { Width = 512, Height = 512, Stretch = Stretch.Fill };
    readonly Canvas canvas = new() { Width = 512, Height = 512, Background = Theme.Background };
    readonly ListView structures = new();
    readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Button render = new() { Content = "生成地图" }, cancel = new() { Content = "取消", Visibility = Visibility.Collapsed };
    readonly Button copyCoordinates = new() { Content = "复制坐标", IsEnabled = false }, locateSelection = new() { Content = "定位", IsEnabled = false }, clearSelection = new() { Content = "清除", IsEnabled = false };
    readonly Button localTab = new() { Content = "已探索地图" }, seedTab = new() { Content = "种子预测" };
    readonly Border loading = new() { Background = Theme.Surface, Padding = new(28), CornerRadius = new(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock loadingText = new() { Text = "准备地图", FontSize = 17, Foreground = Theme.Text, TextAlignment = TextAlignment.Center, MaxWidth = 300, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel legend = new(), featureControls = new();
    readonly Expander seedOptions = new(), legendOptions = new();
    readonly List<FrameworkElement> controls = new();
    readonly DispatcherTimer sortDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    readonly Dictionary<int, string> biomeLabels = new();
    CancellationTokenSource? pending;
    WorldMapImage? raster; SeedMapResult? predicted; RenderedMap? rendered;
    MapSelection? selection;
    bool ready, configuring, fitting = true, filtering, closed, panning, pendingPrediction;
    int generation;
    double zoom = 1; Point? pointerDown; double dragX, dragZ; MapSelection? pointerMarker;
    sealed record RenderedMap(bool Prediction, int Dimension, long Seed, SeedAlgorithm? Algorithm, bool Large, int CenterX, int CenterZ, int Span);
    sealed record MapSelection(string Name, int X, int Z, SeedFeature? Feature = null);

    public WorldMapWindow(SavedWorld? world = null)
    {
        this.world = world; icons = new(world); Title = "地图 · " + (world?.Name ?? "种子探索"); Width = 1260; Height = 870; MinWidth = 1000; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Background; Foreground = Theme.Text; FontFamily = new("Microsoft YaHei UI"); FontSize = 12; Icon = BrandIcon.Image;
        var root = Rows(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto); root.Background = Theme.Background; Content = new Border { Background = Theme.Background, Padding = new(20), Child = root };
        mode.ItemsSource = world == null ? new[] { "种子预测" } : new[] { "已探索地图", "种子预测" }; mode.SelectedIndex = 0;
        dimension.ItemsSource = new[] { "主世界", "下界", "末地" }; dimension.SelectedIndex = 0;
        algorithm.ItemsSource = OfflineSeedMap.Algorithms; algorithm.SelectedItem = world == null ? OfflineSeedMap.Algorithms[15] : world.Source.Bedrock ? null : OfflineSeedMap.Match(world.Version.Length > 0 ? world.Version : world.Source.Version);
        seed.Text = world?.SeedText ?? "123456789"; x.Text = (world?.SpawnX ?? 0).ToString(CultureInfo.InvariantCulture); z.Text = (world?.SpawnZ ?? 0).ToString(CultureInfo.InvariantCulture);
        localTab.Visibility = world == null ? Visibility.Collapsed : Visibility.Visible;
        localTab.Click += (_, _) => mode.SelectedIndex = 0; seedTab.Click += (_, _) => mode.SelectedIndex = world == null ? 0 : 1;
        var header = new DockPanel { Margin = new(0, 0, 0, 16) }; var tabs = Wrap(localTab, seedTab); DockPanel.SetDock(tabs, Dock.Right); header.Children.Add(tabs);
        header.Children.Add(Stack(new TextBlock { Text = world?.Name ?? "种子探索", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { Text = world?.Summary ?? "离线查看生物群系与结构，点击地图记录坐标。", Foreground = Theme.Muted, FontSize = 11, Margin = new(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis })); Put(root, header, 0);

        var side = Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        var viewFields = Columns(1, 1); Add(viewFields, Stack(Label("维度"), dimension), 0); Add(viewFields, Stack(Label("范围 · 方块"), range), 1);
        var coords = Columns(1, 1); Add(coords, Stack(Label("中心 X"), x), 0); Add(coords, Stack(Label("中心 Z"), z), 1); coords.Margin = new(0, 6, 0, 0);
        var actions = new Grid { Margin = new(0, 10, 0, 0) }; actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.Children.Add(render); Grid.SetColumn(cancel, 1); actions.Children.Add(cancel);
        var spawn = Small("出生点", LocateSpawn); spawn.ToolTip = "填入主世界出生点坐标"; Grid.SetColumn(spawn, 2); actions.Children.Add(spawn);
        render.Padding = cancel.Padding = new(10, 7, 10, 7); render.Margin = new(0, 0, 6, 0); cancel.Margin = new(0, 0, 6, 0); render.SetResourceReference(StyleProperty, "Primary");
        seedOptions.Header = "种子与算法"; seedOptions.IsExpanded = world == null; seedOptions.Margin = new(0, 10, 0, 0);
        var seedTools = Wrap(Small("复制种子", () => CopyText(seed.Text, "种子已复制。"))); seedTools.Margin = new(0, 7, 0, 5);
        seedOptions.Content = Stack(seed, seedTools, algorithm, large);
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Foreground = Theme.Muted; explanation.FontSize = 11; explanation.LineHeight = 18;
        legendOptions.Header = "图例"; legendOptions.Content = legend; legendOptions.Margin = new(0, 8, 0, 4);
        var help = new Expander { Header = "地图说明与图例", Content = Stack(explanation, legendOptions), Margin = new(0, 2, 0, 0) };
        Put(side, Scroll(Stack(viewFields, coords, actions, seedOptions, help), 260), 0);

        featureSort.ItemsSource = new[] { "距地图中心", "按名称" }; featureSort.SelectedIndex = 0; featureSort.ToolTip = "按当前地图视区中心的距离排序";
        var compactChoice = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}' FontSize='11' TextTrimming='CharacterEllipsis'/></DataTemplate>");
        featureSort.ItemTemplate = featureFilter.ItemTemplate = algorithm.ItemTemplate = compactChoice;
        algorithm.SetBinding(ToolTipProperty, new Binding("SelectedItem") { Source = algorithm });
        structureHeading.FontWeight = FontWeights.SemiBold; structureHeading.FontSize = 13; structureHeading.Margin = new(0, 0, 0, 8);
        var searchBox = new Grid(); var searchHint = new TextBlock { Text = "⌕  搜索结构或坐标", Foreground = Theme.Muted, Margin = new(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, FontSize = 11 }; searchBox.Children.Add(featureSearch); searchBox.Children.Add(searchHint); featureSearch.ToolTip = "搜索结构名称或坐标";
        var filters = Columns(1.2, 1); Add(filters, featureFilter, 0); Add(filters, featureSort, 1); filters.Margin = new(0, 7, 0, 8);
        featureControls.Children.Add(structureHeading); featureControls.Children.Add(searchBox); featureControls.Children.Add(filters); Put(side, featureControls, 2);
        structures.MinHeight = 45; structures.Margin = new(0, 0, 0, 10); structures.ItemTemplate = StructureTemplate();
        var itemStyle = new Style(typeof(ListViewItem), (Style)FindResource(typeof(ListViewItem))); itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(9, 8, 9, 8))); itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 2, 6))); itemStyle.Setters.Add(new Setter(BackgroundProperty, Theme.Raised)); structures.ItemContainerStyle = itemStyle;
        structureEmpty.Text = "生成地图后显示结构"; structureEmpty.FontSize = 11; structureEmpty.Foreground = Theme.Muted; structureEmpty.TextWrapping = TextWrapping.Wrap; structureEmpty.TextAlignment = TextAlignment.Center; structureEmpty.VerticalAlignment = VerticalAlignment.Center; structureEmpty.Margin = new(10); structureEmpty.IsHitTestVisible = false;
        var structureArea = new Grid(); structureArea.Children.Add(structures); structureArea.Children.Add(structureEmpty); Put(side, structureArea, 3);
        var selectionHeader = new DockPanel(); selectionIcon.Margin = new(0, 0, 9, 0); selectionHeader.Children.Add(selectionIcon); selectedName.FontSize = 14; selectedName.FontWeight = FontWeights.SemiBold; selectedName.TextWrapping = TextWrapping.Wrap; selectedContext.FontSize = 10; selectedContext.Foreground = Theme.Muted; selectedContext.Margin = new(0, 4, 0, 0); selectionHeader.Children.Add(Stack(selectedName, selectedContext));
        selectedCoordinates.FontFamily = new("Consolas,Microsoft YaHei UI"); selectedCoordinates.FontSize = 13; selectedCoordinates.Foreground = Theme.Accent; selectedCoordinates.Margin = new(0, 10, 0, 7); selectedCoordinates.TextWrapping = TextWrapping.Wrap;
        copyCoordinates.Padding = new(10, 7, 10, 7); copyCoordinates.Margin = new(0); copyCoordinates.HorizontalAlignment = HorizontalAlignment.Stretch;
        var locationTools = Wrap(locateSelection, clearSelection); locationTools.Margin = new(0, 6, 0, 0); foreach (Button b in locationTools.Children) { b.Padding = new(10, 5, 10, 5); b.FontSize = 11; }
        selectionNote.FontSize = 10; selectionNote.Foreground = Theme.Muted; selectionNote.TextWrapping = TextWrapping.Wrap; selectionNote.LineHeight = 17; selectionNote.Margin = new(0, 8, 0, 0);
        Put(side, new Border { Child = Stack(selectionHeader, selectedCoordinates, copyCoordinates, locationTools, selectionNote), Background = Theme.Raised, BorderBrush = Theme.Edge, BorderThickness = new(1), CornerRadius = new(8), Padding = new(12) }, 4);
        copyCoordinates.Click += (_, _) => CopySelectedCoordinates(); locateSelection.Click += (_, _) => LocateSelected(); clearSelection.Click += (_, _) => ClearSelection();

        var split = new Grid(); split.ColumnDefinitions.Add(new() { Width = new(264) }); split.ColumnDefinitions.Add(new() { Width = new(16) }); split.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        split.Children.Add(new Border { Child = side, Background = Theme.Surface, BorderBrush = Theme.Edge, BorderThickness = new(1), Padding = new(14), CornerRadius = new(10) });
        var mapPanel = Rows(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto); Grid.SetColumn(mapPanel, 2); split.Children.Add(mapPanel); Put(root, split, 1);
        var toolbar = new DockPanel { Margin = new(0, 0, 0, 8) }; var tools = Wrap(Small("−", () => Zoom(zoom / 1.3)), Small("＋", () => Zoom(zoom * 1.3)), Small("适合", Fit), Small("导出 PNG", ExportImage)); DockPanel.SetDock(tools, Dock.Right); toolbar.Children.Add(tools); toolbar.Children.Add(Wrap(showMarkers, gridLines)); Put(mapPanel, toolbar, 0);
        dirtyHint.FontSize = 11; dirtyHint.Foreground = Theme.Brush("#E6C477"); dirtyHint.TextWrapping = TextWrapping.Wrap; dirtyHint.Margin = new(0, 0, 0, 7); dirtyHint.Visibility = Visibility.Collapsed; Put(mapPanel, dirtyHint, 1);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor); canvas.Children.Add(image); viewport.Content = canvas;
        var stage = new Grid { Background = Theme.Brush("#101A22"), ClipToBounds = true }; stage.Children.Add(viewport);
        loading.Child = Stack(new TextBlock { Text = "◎", FontSize = 32, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 10) }, loadingText); stage.Children.Add(loading);
        var compass = new Border { Child = new TextBlock { Text = "N ↑", FontWeight = FontWeights.SemiBold, Foreground = Theme.Text }, Background = Theme.Brush("#DC101A22"), Padding = new(10, 7, 10, 7), CornerRadius = new(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(12), IsHitTestVisible = false }; stage.Children.Add(compass);
        Put(mapPanel, new Border { Child = stage, BorderBrush = Theme.Edge, BorderThickness = new(1), CornerRadius = new(10), ClipToBounds = true }, 2);
        var foot = new DockPanel { Margin = new(0, 9, 0, 0) }; scale.Foreground = Theme.Muted; scale.FontSize = 11; DockPanel.SetDock(scale, Dock.Right); foot.Children.Add(scale); coordinate.Foreground = Theme.Text; coordinate.FontSize = 11; coordinate.TextTrimming = TextTrimming.CharacterEllipsis; foot.Children.Add(coordinate); Put(mapPanel, foot, 3);
        mapTitle.FontSize = 11; mapTitle.Foreground = Theme.Muted; mapTitle.TextTrimming = TextTrimming.CharacterEllipsis; message.FontSize = 11; message.Foreground = Theme.Muted; message.TextWrapping = TextWrapping.Wrap; message.LineHeight = 18;
        Put(root, Stack(new Border { Height = 1, Background = Theme.Edge, Margin = new(0, 12, 0, 8) }, mapTitle, message), 2);

        controls.AddRange(new FrameworkElement[] { mode, localTab, seedTab, dimension, range, seed, algorithm, large, x, z, spawn });
        render.Click += async (_, _) => await RenderAsync(); cancel.Click += (_, _) => { pending?.Cancel(); loadingText.Text = "正在取消…"; };
        mode.SelectionChanged += (_, _) => { if (ready) ConfigureMode(); };
        foreach (var field in new[] { dimension, range, algorithm }) field.SelectionChanged += (_, _) => SettingsChanged();
        foreach (var field in new[] { seed, x, z }) field.TextChanged += (_, _) => SettingsChanged(); large.Checked += (_, _) => SettingsChanged(); large.Unchecked += (_, _) => SettingsChanged();
        gridLines.Checked += (_, _) => DrawOverlay(); gridLines.Unchecked += (_, _) => DrawOverlay(); showMarkers.Checked += (_, _) => DrawOverlay(); showMarkers.Unchecked += (_, _) => DrawOverlay();
        structures.SelectionChanged += (_, _) => { if (!filtering && structures.SelectedItem is SeedFeature f) SelectFeature(f); };
        featureFilter.SelectionChanged += (_, _) => FilterFeatures(); featureSort.SelectionChanged += (_, _) => FilterFeatures(); featureSearch.TextChanged += (_, _) => { searchHint.Visibility = featureSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FilterFeatures(); };
        viewport.SizeChanged += (_, _) => { if (fitting) Fit(); }; viewport.PreviewMouseWheel += (_, e) => { ZoomAt(e.Delta > 0 ? zoom * 1.2 : zoom / 1.2, e.GetPosition(viewport)); e.Handled = true; };
        viewport.PreviewMouseLeftButtonDown += BeginPointer; viewport.PreviewMouseMove += MovePointer; viewport.PreviewMouseLeftButtonUp += EndPointer; viewport.LostMouseCapture += (_, _) => { pointerDown = null; pointerMarker = null; panning = false; viewport.Cursor = Cursors.Arrow; };
        viewport.ScrollChanged += (_, _) => { if (!filtering && predicted != null && featureSort.SelectedIndex == 0) { sortDelay.Stop(); sortDelay.Start(); } };
        sortDelay.Tick += (_, _) => { sortDelay.Stop(); FilterFeatures(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (pending != null) pending.Cancel(); else ClearSelection(); e.Handled = true; } };
        Closed += (_, _) => { closed = true; generation++; sortDelay.Stop(); pending?.Cancel(); }; ready = true; ConfigureMode(); Loaded += async (_, _) => { if (render.IsEnabled) await RenderAsync(); };
    }

    bool IsPrediction => world == null || mode.SelectedIndex == 1;
    internal bool ReadingWorld => pending != null && !pendingPrediction;
    void ConfigureMode()
    {
        configuring = true; generation++; pending?.Cancel(); sortDelay.Stop(); raster = null; predicted = null; rendered = null; image.Source = null; biomeLabels.Clear(); canvas.Children.Clear(); canvas.Children.Add(image); ClearSelection();
        filtering = true; structures.ItemsSource = null; featureFilter.ItemsSource = null; featureSearch.Text = ""; filtering = false;
        range.ItemsSource = IsPrediction ? new[] { 1024, 4096, 16384 } : new[] { 256, 512, 1024 }; range.SelectedIndex = 1;
        bool supported = world?.Source.Bedrock != true;
        algorithm.Visibility = large.Visibility = IsPrediction && supported ? Visibility.Visible : Visibility.Collapsed;
        featureControls.Visibility = structures.Visibility = structureEmpty.Visibility = IsPrediction && supported ? Visibility.Visible : Visibility.Collapsed;
        structureEmpty.Text = "生成地图后显示结构";
        seed.IsReadOnly = !IsPrediction; render.IsEnabled = pending == null && (!IsPrediction || supported); seedOptions.IsExpanded = world == null; legendOptions.IsExpanded = !IsPrediction;
        localTab.Background = !IsPrediction ? Theme.Brush("#154735") : Theme.Raised; seedTab.Background = IsPrediction ? Theme.Brush("#154735") : Theme.Raised; localTab.Foreground = !IsPrediction ? Theme.Accent : Theme.Text; seedTab.Foreground = IsPrediction ? Theme.Accent : Theme.Text;
        explanation.Text = IsPrediction ? !supported ? "基岩版离线预测尚未适配。可切换已探索地图查看本地世界。" : "预测原版生物群系与部分结构，不包含玩家建筑。模组、数据包及跨版本区块可能不同。末地要塞标记为要塞预测位置，不是传送门房间的精确坐标。" : "读取已保存的地表方块。灰格表示未探索或无法读取。下界显示顶部以下的切面，部分较新或模组区块格式可能不支持。";
        loading.Visibility = Visibility.Visible; loadingText.Text = IsPrediction && !supported ? "基岩版预测尚未适配" : "选择位置，生成地图"; mapTitle.Text = "尚未生成地图"; dirtyHint.Visibility = Visibility.Collapsed;
        message.Text = IsPrediction && !supported ? explanation.Text : "拖动平移 · 滚轮缩放 · 点击地图或标记选择坐标"; coordinate.Text = ""; scale.Text = ""; structureHeading.Text = "结构位置"; BuildLegend(); configuring = false;
    }
    void SettingsChanged()
    {
        if (!ready || configuring || pending != null || rendered == null) return;
        bool same = rendered.Prediction == IsPrediction && rendered.Dimension == dimension.SelectedIndex && range.SelectedItem is int span && span == rendered.Span
            && int.TryParse(x.Text, out int cx) && cx == rendered.CenterX && int.TryParse(z.Text, out int cz) && cz == rendered.CenterZ
            && (!rendered.Prediction || long.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value == rendered.Seed && Equals(algorithm.SelectedItem, rendered.Algorithm) && (large.IsChecked == true) == rendered.Large);
        dirtyHint.Visibility = same ? Visibility.Collapsed : Visibility.Visible; dirtyHint.Text = "设置已更改；当前地图仍为“" + DimensionName(rendered.Dimension) + "”。点击生成地图更新。";
    }
    void LocateSpawn()
    {
        if (world == null) { message.Text = "手动种子地图没有已读取的出生点。可输入目标坐标生成地图。"; return; }
        x.Text = world.SpawnX.ToString(CultureInfo.InvariantCulture); z.Text = world.SpawnZ.ToString(CultureInfo.InvariantCulture);
        if (dimension.SelectedIndex != 0) dimension.SelectedIndex = 0;
        if (rendered?.Dimension == 0 && ContainsWorldPoint(world.SpawnX, world.SpawnZ)) { SelectMapPoint(world.SpawnX, world.SpawnZ, "出生点"); CenterOn(world.SpawnX, world.SpawnZ); }
        message.Text = "已填入主世界出生点；如不在当前范围内，请生成地图。";
    }
    async Task RenderAsync()
    {
        if (pending != null || closed) return;
        if (IsPrediction && world?.Source.Bedrock == true) { message.Text = explanation.Text; return; }
        if (!int.TryParse(x.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cx) || !int.TryParse(z.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cz) || range.SelectedItem is not int span || dimension.SelectedIndex is < 0 or > 2) { message.Text = "请输入有效的 X、Z 坐标和地图范围。"; return; }
        bool prediction = IsPrediction; long value = 0; var alg = algorithm.SelectedItem as SeedAlgorithm;
        if (prediction && (!long.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || alg == null)) { seedOptions.IsExpanded = true; message.Text = "请输入整数种子，并明确选择已支持的 Java 算法版本。"; return; }
        var request = new RenderedMap(prediction, dimension.SelectedIndex, value, alg, large.IsChecked == true, cx, cz, span);
        var cts = pending = new(); pendingPrediction = prediction; int epoch = ++generation; foreach (var control in controls) control.IsEnabled = false; render.IsEnabled = false; cancel.Visibility = Visibility.Visible; sortDelay.Stop();
        image.Source = null; raster = null; predicted = null; rendered = null; ClearSelection(); filtering = true; structures.ItemsSource = null; featureFilter.ItemsSource = null; filtering = false; canvas.Children.Clear(); canvas.Children.Add(image); biomeLabels.Clear();
        loading.Visibility = Visibility.Visible; dirtyHint.Visibility = Visibility.Collapsed; mapTitle.Text = "正在生成地图…"; loadingText.Text = message.Text = prediction ? "正在离线计算…" : "正在读取已探索区块…";
        if (prediction) { structureEmpty.Text = "正在计算结构位置…"; structureEmpty.Visibility = Visibility.Visible; }
        try
        {
            var progress = new Progress<string>(s => { if (!closed && epoch == generation && ReferenceEquals(pending, cts) && !cts.IsCancellationRequested) message.Text = loadingText.Text = s; });
            SeedMapResult? result = null; WorldMapImage map;
            if (prediction) { await icons.LoadAsync(cts.Token); result = await Task.Run(() => OfflineSeedMap.Render(world?.Source.Bedrock == true, alg!, value, cx, cz, span, request.Dimension, request.Large, progress, cts.Token), cts.Token); map = result.Image; }
            else map = await Task.Run(() => WorldMapRenderer.Render(world!, cx, cz, span, request.Dimension, progress, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); if (closed || epoch != generation) return;
            int bitmapWidth = map.DetailPixels != null ? map.DetailWidth : map.Width;
            var bitmap = BitmapSource.Create(bitmapWidth, bitmapWidth, 96, 96, PixelFormats.Bgra32, null, map.DetailPixels ?? map.Pixels, bitmapWidth * 4); bitmap.Freeze(); raster = map; predicted = result; rendered = request; image.Source = bitmap;
            if (result != null)
            {
                filtering = true; featureFilter.ItemsSource = new[] { "全部结构" }.Concat(result.Features.Where(f => f.Name != "古迹废墟").Select(f => f.Name).Distinct().OrderBy(n => n)).ToArray(); featureFilter.SelectedIndex = 0; filtering = false;
                seedOptions.IsExpanded = false; message.Text = $"已预测 {result.Features.Count(f => f.Name != "古迹废墟")} 个结构 · 点击标记查看坐标；结果可能与实际生成不同。";
            }
            else
            {
                message.Text = map.Chunks == 0 ? "此范围没有可读取的已探索区块，请调整中心或扩大范围。" : $"已读取 {map.Chunks} 个区块" + (map.Unreadable > 0 ? $" · {map.Unreadable} 处未能读取，已留空。" : " · 存档未修改。");
                message.Text += map.TexturedBlocks > 0 ? " 已启用方块贴图。" : world?.Source.Bedrock == true ? " 当前使用地表配色。" : " 未找到匹配的本机 Java 贴图，当前使用地表配色。";
            }
            mapTitle.Text = (prediction ? "离线预测 · 种子 " + request.Seed.ToString(CultureInfo.InvariantCulture) + " · " + request.Algorithm + (request.Large ? " · 大型生物群系" : "") : "已探索地图") + " · " + DimensionName(request.Dimension) + $" · 中心 {cx}, {cz}";
            loading.Visibility = Visibility.Collapsed; coordinate.Text = $"中心 X {cx}    Z {cz}"; BuildLegend(); Fit(); FilterFeatures(); UpdateSelection(); SettingsChanged();
        }
        catch (OperationCanceledException) { if (!closed && epoch == generation) { ClearMapResult(); mapTitle.Text = "生成已取消"; message.Text = loadingText.Text = "地图生成已取消，可调整设置后重试。"; } }
        catch (Exception e) { if (!closed && epoch == generation) { ClearMapResult(); mapTitle.Text = "地图未生成"; message.Text = loadingText.Text = "地图生成未完成：" + e.Message; } }
        finally { if (ReferenceEquals(pending, cts)) { pending = null; foreach (var control in controls) control.IsEnabled = true; render.IsEnabled = !IsPrediction || world?.Source.Bedrock != true; cancel.Visibility = Visibility.Collapsed; if (!closed) SettingsChanged(); } cts.Dispose(); }
    }
    void ClearMapResult()
    {
        image.Source = null; raster = null; predicted = null; rendered = null; filtering = true; structures.ItemsSource = null; featureFilter.ItemsSource = null; filtering = false;
        ClearSelection(); loading.Visibility = Visibility.Visible; coordinate.Text = scale.Text = ""; dirtyHint.Visibility = Visibility.Collapsed;
        structureEmpty.Text = "生成地图后显示结构"; structureEmpty.Visibility = IsPrediction ? Visibility.Visible : Visibility.Collapsed;
    }

    IEnumerable<SeedFeature> VisibleFeatures
    {
        get
        {
            string query = featureSearch.Text.Trim(); string? filter = featureFilter.SelectedItem as string;
            var features = predicted?.Features.Where(f => f.Name != "古迹废墟" && (filter == null || filter == "全部结构" || f.Name == filter) && (query.Length == 0 || (f.Name + " " + f.X + " " + f.Z).Contains(query, StringComparison.OrdinalIgnoreCase))) ?? Enumerable.Empty<SeedFeature>();
            var center = ViewCenter(); return featureSort.SelectedIndex == 1 ? features.OrderBy(f => f.Name).ThenBy(f => Distance(f, center)) : features.OrderBy(f => Distance(f, center)).ThenBy(f => f.Name);
        }
    }
    static double Distance(SeedFeature f, Point p) { double dx = f.X - p.X, dz = f.Z - p.Y; return dx * dx + dz * dz; }
    Point ViewCenter()
    {
        if (raster == null) return new(rendered?.CenterX ?? 0, rendered?.CenterZ ?? 0);
        var p = viewport.TranslatePoint(new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2), canvas);
        return new(raster.MinX + Math.Clamp(p.X / 512, 0, 1) * raster.Width * raster.BlocksPerPixel, raster.MinZ + Math.Clamp(p.Y / 512, 0, 1) * raster.Width * raster.BlocksPerPixel);
    }
    void FilterFeatures()
    {
        if (filtering || !ready) return; var list = VisibleFeatures.ToArray(); filtering = true;
        try { structures.ItemsSource = list; structures.SelectedItem = selection?.Feature != null && list.Contains(selection.Feature) ? selection.Feature : null; }
        finally { filtering = false; }
        if (selection?.Feature != null && !list.Contains(selection.Feature)) ClearSelection();
        structureHeading.Text = predicted == null ? "结构位置" : $"结构位置 · {list.Length} / {predicted.Features.Count(f => f.Name != "古迹废墟")}";
        structureEmpty.Visibility = IsPrediction && list.Length == 0 ? Visibility.Visible : Visibility.Collapsed; structureEmpty.Text = predicted == null ? "生成地图后显示结构" : "没有符合条件的结构\n试试其它类型或关键词"; DrawOverlay();
    }
    void SelectFeature(SeedFeature feature, bool center = false)
    {
        if (predicted == null || !predicted.Features.Contains(feature) || rendered == null) return;
        selection = new(feature.Name, feature.X, feature.Z, feature); filtering = true;
        try { structures.SelectedItem = feature; if (structures.Items.Contains(feature)) structures.ScrollIntoView(feature); }
        finally { filtering = false; }
        seedOptions.IsExpanded = false; UpdateSelection(); if (center) CenterOn(feature.X, feature.Z); DrawOverlay();
    }
    void SelectMapPoint(int mx, int mz, string name = "地图位置")
    {
        if (!ContainsWorldPoint(mx, mz) || rendered == null) return; selection = new(name, mx, mz); filtering = true; structures.SelectedItem = null; filtering = false; if (rendered.Prediction) seedOptions.IsExpanded = false; UpdateSelection(); DrawOverlay();
    }
    void ClearSelection()
    {
        selection = null; filtering = true; structures.SelectedItem = null; filtering = false; UpdateSelection(); if (ready) DrawOverlay();
    }
    string SelectionCoordinates => selection == null ? "" : $"X: {selection.X}, Z: {selection.Z}";
    void UpdateSelection()
    {
        selectedName.Text = selection?.Name ?? "位置详情"; selectedCoordinates.Text = selection == null ? "点击地图或结构选择坐标" : SelectionCoordinates;
        selectedContext.Text = selection == null || rendered == null ? "标记与列表可以互相选择" : DimensionName(rendered.Dimension) + (rendered.Prediction ? " · 预测地图" : " · 已探索地图");
        selectionIcon.Source = selection == null ? null : icons.Get(selection.Name); selectionIcon.Visibility = selectionIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;
        copyCoordinates.IsEnabled = locateSelection.IsEnabled = clearSelection.IsEnabled = selection != null;
        copyCoordinates.Visibility = locateSelection.Visibility = clearSelection.Visibility = selection == null ? Visibility.Collapsed : Visibility.Visible;
        selectionNote.Text = selection?.Name == "末地要塞" ? "要塞预测位置；不是传送门房间的精确坐标。" : selection?.Feature != null ? "结构位置为预测值，请以实际世界为准。" : selection == null ? "拖动可平移地图，滚轮可缩放。" : "坐标对应当前已生成地图的维度。";
    }
    void CopySelectedCoordinates() { if (selection != null) CopyText(SelectionCoordinates, "坐标已复制 · " + SelectionCoordinates); }
    void CopyText(string text, string success)
    {
        try { Clipboard.SetText(text); message.Text = success; }
        catch (System.Runtime.InteropServices.ExternalException) { message.Text = "剪贴板正被占用，请稍后重试。"; }
    }
    void LocateSelected()
    {
        if (selection == null || raster == null) return;
        double fit = Math.Min(Math.Max(1, viewport.ActualWidth - 4) / 512, Math.Max(1, viewport.ActualHeight - 4) / 512);
        if (zoom < fit * 1.8) Zoom(fit * 1.8); CenterOn(selection.X, selection.Z); message.Text = "已定位 · " + selection.Name + " · " + SelectionCoordinates;
    }
    bool ContainsWorldPoint(int mx, int mz) => raster != null && mx >= raster.MinX && mz >= raster.MinZ && (long)mx < (long)raster.MinX + raster.Width * raster.BlocksPerPixel && (long)mz < (long)raster.MinZ + raster.Width * raster.BlocksPerPixel;
    void CenterOn(int mx, int mz)
    {
        if (raster == null) return; var p = canvas.TranslatePoint(new Point((mx - raster.MinX) / (double)(raster.Width * raster.BlocksPerPixel) * 512, (mz - raster.MinZ) / (double)(raster.Width * raster.BlocksPerPixel) * 512), viewport);
        viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset + p.X - viewport.ActualWidth / 2); viewport.ScrollToVerticalOffset(viewport.VerticalOffset + p.Y - viewport.ActualHeight / 2);
    }

    void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        if (raster == null || pending != null || e.OriginalSource is not DependencyObject hit || !InsideCanvas(hit)) return;
        pointerDown = e.GetPosition(viewport); dragX = viewport.HorizontalOffset; dragZ = viewport.VerticalOffset; pointerMarker = MarkerAt(hit); panning = false;
    }
    bool InsideCanvas(DependencyObject? hit)
    {
        while (hit != null) { if (ReferenceEquals(hit, canvas)) return true; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit); } return false;
    }
    static MapSelection? MarkerAt(DependencyObject? hit)
    {
        while (hit != null) { if (hit is FrameworkElement { Tag: MapSelection marker }) return marker; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit); } return null;
    }
    void MovePointer(object sender, MouseEventArgs e)
    {
        if (pointerDown != null && e.LeftButton != MouseButtonState.Pressed) { pointerDown = null; pointerMarker = null; panning = false; if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture(); viewport.Cursor = Cursors.Arrow; }
        if (pointerDown is Point start && e.LeftButton == MouseButtonState.Pressed)
        {
            var point = e.GetPosition(viewport);
            if (!panning && (Math.Abs(point.X - start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance)) { panning = true; fitting = false; viewport.CaptureMouse(); viewport.Cursor = Cursors.ScrollAll; }
            if (panning) { viewport.ScrollToHorizontalOffset(dragX + start.X - point.X); viewport.ScrollToVerticalOffset(dragZ + start.Y - point.Y); e.Handled = true; return; }
        }
        if (raster == null) return; var p = e.GetPosition(canvas); if (p.X < 0 || p.Y < 0 || p.X >= 512 || p.Y >= 512) return;
        int px = Math.Clamp((int)(p.X / 512 * raster.Width), 0, raster.Width - 1), pz = Math.Clamp((int)(p.Y / 512 * raster.Width), 0, raster.Width - 1);
        coordinate.Text = $"X {raster.MinX + px * raster.BlocksPerPixel}    Z {raster.MinZ + pz * raster.BlocksPerPixel}";
        if (predicted != null) coordinate.Text += "    " + BiomeLabel(predicted.Biomes[pz * raster.Width + px]);
    }
    void EndPointer(object sender, MouseButtonEventArgs e)
    {
        if (pointerDown == null) return;
        bool moved = panning; var marker = pointerMarker; var p = e.GetPosition(canvas); pointerDown = null; pointerMarker = null; panning = false; if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture(); viewport.Cursor = Cursors.Arrow;
        if (!moved && raster != null && p.X >= 0 && p.Y >= 0 && p.X < 512 && p.Y < 512)
        {
            if (marker?.Feature is SeedFeature f) SelectFeature(f);
            else if (marker != null) SelectMapPoint(marker.X, marker.Z, marker.Name);
            else SelectMapPoint(raster.MinX + (int)(p.X / 512 * raster.Width) * raster.BlocksPerPixel, raster.MinZ + (int)(p.Y / 512 * raster.Width) * raster.BlocksPerPixel);
        }
        e.Handled = true;
    }
    void Zoom(double value) => ZoomAt(value, new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2));
    void ZoomAt(double value, Point anchor)
    {
        fitting = false; Point local = viewport.TranslatePoint(anchor, canvas); zoom = Math.Clamp(value, .25, 12); canvas.LayoutTransform = new ScaleTransform(zoom, zoom); viewport.UpdateLayout();
        Point after = canvas.TranslatePoint(local, viewport); viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset + after.X - anchor.X); viewport.ScrollToVerticalOffset(viewport.VerticalOffset + after.Y - anchor.Y); DrawOverlay();
    }
    void Fit()
    {
        if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        Zoom(Math.Min((viewport.ActualWidth - 4) / 512, (viewport.ActualHeight - 4) / 512)); fitting = true; viewport.ScrollToHorizontalOffset(Math.Max(0, (viewport.ExtentWidth - viewport.ViewportWidth) / 2)); viewport.ScrollToVerticalOffset(Math.Max(0, (viewport.ExtentHeight - viewport.ViewportHeight) / 2));
    }
    void DrawOverlay()
    {
        canvas.Children.Clear(); canvas.Children.Add(image); if (raster == null) return;
        if (gridLines.IsChecked == true) for (int n = 1; n < 8; n++)
        {
            double p = n * 64; canvas.Children.Add(new Line { X1 = p, X2 = p, Y1 = 0, Y2 = 512, Stroke = Theme.Brush("#507C929A"), StrokeThickness = .8 / zoom, IsHitTestVisible = false }); canvas.Children.Add(new Line { X1 = 0, X2 = 512, Y1 = p, Y2 = p, Stroke = Theme.Brush("#507C929A"), StrokeThickness = .8 / zoom, IsHitTestVisible = false });
            GridLabel((raster.MinX + n * raster.Width * raster.BlocksPerPixel / 8).ToString(), p + 2 / zoom, 2 / zoom); GridLabel((raster.MinZ + n * raster.Width * raster.BlocksPerPixel / 8).ToString(), 2 / zoom, p + 2 / zoom);
        }
        if (showMarkers.IsChecked == true)
        {
            var occupied = new HashSet<(int, int)>(); int count = 0;
            foreach (var f in VisibleFeatures.OrderBy(f => selection?.Feature == f ? 0 : 1))
            {
                double px = (f.X - raster.MinX) / (double)(raster.Width * raster.BlocksPerPixel) * 512, pz = (f.Z - raster.MinZ) / (double)(raster.Width * raster.BlocksPerPixel) * 512;
                var cell = ((int)Math.Floor(px * zoom / 28), (int)Math.Floor(pz * zoom / 28)); if (!occupied.Add(cell)) continue;
                AddMarker(new(f.Name, f.X, f.Z, f)); if (++count >= 1000) break;
            }
            if (world != null && rendered is { Prediction: false, Dimension: 0 } && selection?.Name != "出生点") AddMarker(new("出生点", world.SpawnX, world.SpawnZ));
            if (selection is { Feature: null }) AddMarker(selection);
        }
        scale.Text = $"{raster.Width * raster.BlocksPerPixel:N0} × {raster.Width * raster.BlocksPerPixel:N0} 方块  ·  {zoom * 100:0}%";
    }
    void GridLabel(string text, double px, double pz)
    {
        var label = new TextBlock { Text = text, FontSize = 9 / zoom, Foreground = Brushes.White, Background = Theme.Brush("#B0101A22"), IsHitTestVisible = false }; Canvas.SetLeft(label, px); Canvas.SetTop(label, pz); canvas.Children.Add(label);
    }
    void AddMarker(MapSelection marker)
    {
        if (!ContainsWorldPoint(marker.X, marker.Z) || raster == null) return;
        double px = (marker.X - raster.MinX) / (double)(raster.Width * raster.BlocksPerPixel) * 512, pz = (marker.Z - raster.MinZ) / (double)(raster.Width * raster.BlocksPerPixel) * 512;
        bool chosen = selection == marker; var color = chosen ? Theme.Accent : marker.Name.Contains("海") ? Theme.Brush("#A5D8E3") : marker.Name.Contains("传送") || marker.Name == "末地要塞" ? Theme.Brush("#C7ADF0") : Theme.Brush("#F2D3A2");
        double size = (chosen ? 29 : 24) / zoom; var border = new Border { Width = size, Height = size, CornerRadius = new(5 / zoom), Background = Theme.Brush("#F014232D"), BorderBrush = color, BorderThickness = new((chosen ? 2 : 1) / zoom), ToolTip = marker.Name + $" · X {marker.X} / Z {marker.Z}", Tag = marker, Cursor = Cursors.Hand };
        if (icons.Get(marker.Name) is { } icon) { var img = new Image { Source = icon, Margin = new(1 / zoom) }; RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor); border.Child = img; }
        else border.Child = new TextBlock { Text = marker.Name == "出生点" ? "⌂" : marker.Feature == null ? "+" : marker.Name[..1], FontSize = 12 / zoom, FontWeight = FontWeights.SemiBold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Canvas.SetLeft(border, px - size / 2); Canvas.SetTop(border, pz - size / 2); if (chosen) Panel.SetZIndex(border, 10); canvas.Children.Add(border);
    }
    string BiomeLabel(int biome)
    {
        if (predicted == null) return ""; if (!biomeLabels.TryGetValue(biome, out string? label)) biomeLabels[biome] = label = OfflineSeedMap.BiomeDisplayName(predicted.Algorithm, biome); return label;
    }
    void BuildLegend()
    {
        legend.Children.Clear();
        if (predicted != null)
        {
            foreach (var group in predicted.Biomes.GroupBy(b => b).OrderByDescending(g => g.Count()).Take(7))
            {
                int rgb = OfflineSeedMap.BiomeColor(predicted.Algorithm, group.Key); AddLegend(BiomeLabel(group.Key), new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
            }
        }
        else foreach (var (text, color) in new[] { ("植被", "#6B9758"), ("水域", "#426E9A"), ("建筑 / 地表方块", "#B4936A"), ("未探索 / 无法读取", "#35434B") }) AddLegend(text, Theme.Brush(color));
    }
    void AddLegend(string text, Brush color)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 5) }; row.Children.Add(new Border { Width = 9, Height = 9, Background = color, CornerRadius = new(2), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(new TextBlock { Text = text, Foreground = Theme.Muted, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis }); legend.Children.Add(row);
    }
    void ExportImage()
    {
        if (image.Source is not BitmapSource) { message.Text = "请先生成地图。"; return; }
        var dialog = new SaveFileDialog { Title = "导出地图图片", Filter = "PNG 图片|*.png", FileName = "BlockDock-地图.png" }; if (dialog.ShowDialog(this) != true) return;
        try { ExportImageTo(dialog.FileName); message.Text = "地图已导出，包含当前标记与网格。"; }
        catch (Exception e) { message.Text = "导出未完成：" + e.Message; }
    }
    void ExportImageTo(string path)
    {
        if (image.Source is not BitmapSource || rendered == null) throw new IOException("请先生成地图。");
        string output = System.IO.Path.GetFullPath(path); WorldFiles.NoLinks(output);
        if (world != null && (WorldFiles.Same(world.Folder, output) || output.StartsWith(System.IO.Path.GetFullPath(world.Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("请将地图图片保存到世界目录之外。");
        string? temporary = null;
        try
        {
            int size = Math.Max(1536, raster?.DetailWidth ?? 0); var bitmap = new RenderTargetBitmap(size, size, size / 512d * 96, size / 512d * 96, PixelFormats.Pbgra32); var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(canvas) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 512, 512)); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, ".blockdock-map-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(file);
            WorldFiles.NoLinks(output); File.Move(temporary, output, true); temporary = null;
        }
        finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } }
    }
    DataTemplate StructureTemplate()
    {
        var dock = new FrameworkElementFactory(typeof(DockPanel)); var icon = new FrameworkElementFactory(typeof(Image)); icon.SetValue(WidthProperty, 26d); icon.SetValue(HeightProperty, 26d); icon.SetValue(MarginProperty, new Thickness(0, 0, 9, 0)); icon.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor); icon.SetBinding(Image.SourceProperty, new Binding("Name") { Converter = new FeatureIconConverter(icons) }); dock.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel)); var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); name.SetValue(TextBlock.FontSizeProperty, 12d); name.SetBinding(TextBlock.TextProperty, new Binding("Name")); text.AppendChild(name);
        var coords = new FrameworkElementFactory(typeof(TextBlock)); coords.SetValue(TextBlock.FontSizeProperty, 10d); coords.SetValue(TextBlock.ForegroundProperty, Theme.Muted); coords.SetValue(MarginProperty, new Thickness(0, 4, 0, 0)); var binding = new MultiBinding { StringFormat = "X {0}   Z {1}" }; binding.Bindings.Add(new Binding("X")); binding.Bindings.Add(new Binding("Z")); coords.SetBinding(TextBlock.TextProperty, binding); text.AppendChild(coords); dock.AppendChild(text); return new DataTemplate { VisualTree = dock };
    }
    static string DimensionName(int dim) => dim switch { 1 => "下界", 2 => "末地", _ => "主世界" };
    static Button Small(string text, Action action) { var b = new Button { Content = text, Padding = new(9, 7, 9, 7), FontSize = 11, Margin = new(0, 0, 6, 0) }; b.Click += (_, _) => action(); return b; }
    static TextBlock Label(string text) => new() { Text = text, Foreground = Theme.Muted, FontSize = 10, Margin = new(0, 0, 0, 5) };
    static StackPanel Stack(params UIElement[] items) { var p = new StackPanel(); foreach (var item in items) p.Children.Add(item); return p; }
    static WrapPanel Wrap(params UIElement[] items) { var p = new WrapPanel(); foreach (var item in items) p.Children.Add(item); return p; }
    static ScrollViewer Scroll(UIElement item, double maximum) => new() { Content = item, MaxHeight = maximum, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    static Grid Rows(params GridLength[] heights) { var g = new Grid(); foreach (var height in heights) g.RowDefinitions.Add(new() { Height = height }); return g; }
    static Grid Columns(double a, double b) { var g = new Grid(); g.ColumnDefinitions.Add(new() { Width = new(a, GridUnitType.Star) }); g.ColumnDefinitions.Add(new() { Width = new(8) }); g.ColumnDefinitions.Add(new() { Width = new(b, GridUnitType.Star) }); return g; }
    static void Add(Grid g, UIElement item, int col) { Grid.SetColumn(item, col * 2); g.Children.Add(item); }
    static void Put(Grid g, UIElement item, int row) { Grid.SetRow(item, row); g.Children.Add(item); }
    sealed class FeatureIconConverter(StructureMapIcons icons) : IValueConverter
    {
        public object? Convert(object value, Type type, object parameter, CultureInfo culture) => icons.Get(value?.ToString() ?? "");
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
