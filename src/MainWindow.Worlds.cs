using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BlockDock;

public partial class MainWindow
{
    readonly ComboBox worldSource = new() { MinWidth = 180, MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox worldSearch = new() { ToolTip = "搜索世界名称、版本或种子" };
    readonly ListView worldList = new();
    readonly TextBlock worldCount = new(), worldMessage = new(), worldName = new(), worldDetails = new();
    readonly TextBox worldSeed = new() { IsReadOnly = true };
    readonly Image worldCover = new() { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
    readonly TextBlock worldEmpty = new(), worldEdition = new();
    readonly WrapPanel worldActions = new();
    readonly Button worldCancel = new() { Content = "取消操作", Visibility = Visibility.Collapsed };
    List<WorldSource> worldSources = new(); List<SavedWorld> savedWorlds = new();
    CancellationTokenSource? worldScanCts, worldOperationCts;
    string? requestedWorldRoot;
    bool worldScanning;

    void BuildWorlds()
    {
        var root = Rows(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        worldSource.ItemTemplate = Template("<TextBlock Text='{Binding}' TextTrimming='CharacterEllipsis'/>");
        worldSource.SelectionChanged += (_, _) => FilterWorlds(); worldSearch.TextChanged += (_, _) => FilterWorlds();
        var filters = Cols(new(1, GridUnitType.Star), new(12), new(1, GridUnitType.Star));
        Row(filters, worldSource, 0);
        var search = new Grid(); var searchHint = new TextBlock { Text = "⌕  搜索世界、版本或种子", Foreground = Theme.Muted, IsHitTestVisible = false, Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        search.Children.Add(worldSearch); search.Children.Add(searchHint); worldSearch.TextChanged += (_, _) => searchHint.Visibility = worldSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Row(filters, search, 0, 2);
        var tools = H(Button("导入地图", async (_, _) => await ImportWorldAsync(), true), Button("导入文件夹", async (_, _) => await ImportWorldAsync(true)),
            Button("种子地图", (_, _) => new WorldMapWindow { Owner = this }.Show()), Button("刷新", async (_, _) => await RefreshWorldsAsync()));
        tools.Margin = new(0, 10, 0, -4); foreach (Button button in tools.Children) { button.Padding = new(13, 7, 13, 7); button.Margin = new(0, 0, 8, 4); }
        Row(root, WorldPanel(V(filters, tools), 14), 0);
        worldCount.Foreground = Theme.Muted; worldCount.Background = Theme.Surface; worldCount.Padding = new(12, 6, 12, 6); worldCount.Margin = new(0, 10, 0, 8); worldCount.FontSize = 12; worldCount.TextTrimming = TextTrimming.CharacterEllipsis; Row(root, worldCount, 1);
        var itemStyle = new Style(typeof(ListViewItem), (Style)FindResource(typeof(ListViewItem)));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Theme.Raised)); itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(12))); itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 10)));
        worldList.ItemContainerStyle = itemStyle;
        var cardPresenter = new FrameworkElementFactory(typeof(ContentControl));
        cardPresenter.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        cardPresenter.SetBinding(ContentControl.ContentProperty, new Binding { Converter = new WorldCardConverter(this) });
        worldList.ItemTemplate = new DataTemplate { VisualTree = cardPresenter };
        worldList.SelectionChanged += (_, _) => SelectWorld();
        worldList.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject hit && FindWorldButton(hit) == null && ItemsControl.ContainerFromElement(worldList, hit) is ListViewItem { DataContext: SavedWorld selected } && worldOperationCts == null) new WorldMapWindow(selected) { Owner = this }.Show(); };
        worldEmpty.Text = "还没有世界\n导入地图，或在游戏中创建并保存世界。"; worldEmpty.TextWrapping = TextWrapping.Wrap; worldEmpty.TextAlignment = TextAlignment.Center; worldEmpty.LineHeight = 28; worldEmpty.Foreground = Theme.Muted; worldEmpty.FontSize = 14; worldEmpty.Margin = new(24); worldEmpty.HorizontalAlignment = HorizontalAlignment.Center; worldEmpty.VerticalAlignment = VerticalAlignment.Center; worldEmpty.IsHitTestVisible = false;
        var listArea = new Grid(); listArea.Children.Add(worldList); listArea.Children.Add(worldEmpty);
        worldName.FontSize = 18; worldName.FontWeight = FontWeights.SemiBold; worldName.TextWrapping = worldDetails.TextWrapping = TextWrapping.Wrap;
        worldEdition.FontSize = 11; worldEdition.Foreground = Theme.Accent; worldEdition.Margin = new(0, 6, 0, 0);
        worldDetails.Foreground = Theme.Muted; worldDetails.FontSize = 12; worldDetails.LineHeight = 21;
        worldSeed.FontFamily = new FontFamily("Consolas,Microsoft YaHei UI"); worldSeed.FontSize = 12; worldSeed.Padding = new(10, 8, 10, 8);
        worldActions.Children.Add(Button("查看地图", (_, _) => { if (worldList.SelectedItem is SavedWorld w) new WorldMapWindow(w) { Owner = this }.Show(); }, true));
        worldActions.Children.Add(Button("复制种子", (_, _) => { if (worldList.SelectedItem is SavedWorld { Seed: not null } w) CopyWorldSeed(w); }));
        worldActions.Children.Add(Button("一键备份", async (_, _) => await ExportWorldAsync(true)));
        worldActions.Children.Add(Button("导出", async (_, _) => await ExportWorldAsync(false)));
        worldActions.Children.Add(Button("复制到版本", async (_, _) => await CopyWorldAsync()));
        worldActions.Children.Add(Button("打开文件夹", (_, _) => { if (worldList.SelectedItem is SavedWorld w) Open(w.Folder); }));
        worldActions.Children.Add(Button("移入回收目录", async (_, _) => await RecycleWorldAsync()));
        foreach (Button button in worldActions.Children) { button.Width = 114; button.FontSize = 12; button.Padding = new(8, 8, 8, 8); button.Margin = new(0, 0, 8, 8); }
        ((Button)worldActions.Children[0]).Width = 236;
        var detailHeader = Cols(new(64), new(12), new(1, GridUnitType.Star)); var selectedCover = WorldCoverFrame(worldCover, 64, 64, false);
        Row(detailHeader, selectedCover, 0); Row(detailHeader, V(worldName, worldEdition), 0, 2);
        var detail = WorldPanel(V(detailHeader, Gap(12), worldDetails, Gap(12), Text("世界种子", 12, true), Gap(5), worldSeed, Gap(14), worldActions), 16);
        var columns = Cols(new(1, GridUnitType.Star), new(14), new(280)); Row(columns, WorldPanel(listArea, 10), 0); Row(columns, Scroll(detail), 0, 2); Row(root, columns, 2);
        worldMessage.TextWrapping = TextWrapping.Wrap; worldMessage.Foreground = Theme.Muted;
        worldMessage.FontSize = 12; worldMessage.Text = "选择世界查看地图，或直接备份存档。";
        worldCancel.Click += (_, _) => worldOperationCts?.Cancel();
        var storageButtons = H(worldCancel, Button("备份目录", (_, _) => { string folder = Path.Combine(Settings.DataPath, "world-backups"); Directory.CreateDirectory(folder); Open(folder); }), Button("回收目录", (_, _) => { if (worldList.SelectedItem is SavedWorld w) { string folder = Path.Combine(w.Source.Root, ".blockdock-recycle"); Directory.CreateDirectory(folder); Open(folder); } }));
        foreach (Button button in storageButtons.Children) { button.Padding = new(10, 5, 10, 5); button.FontSize = 11; } storageButtons.Margin = new(0, 7, 0, 0);
        var footer = WorldPanel(V(worldMessage, storageButtons), 10); footer.Margin = new(0, 10, 0, 0); Row(root, footer, 3);
        Page("worlds", root); SelectWorld();
    }

    static Border WorldPanel(UIElement content, double padding) => new() { Child = content, Background = Theme.Surface, BorderBrush = Theme.Edge, BorderThickness = new(1), CornerRadius = new(10), Padding = new(padding) };
    static Button? FindWorldButton(DependencyObject? child)
    {
        while (child != null) { if (child is Button button) return button; child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); }
        return null;
    }
    static Border WorldCoverFrame(Image image, double width, double height, bool caption)
    {
        var background = new Grid { Width = width, Height = height, Background = Theme.Background, ClipToBounds = true };
        var placeholder = V(new TextBlock { Text = "\uE707", FontFamily = new("Segoe MDL2 Assets"), FontSize = caption ? 27 : 23, Foreground = Theme.Brush("#526875"), HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = caption ? "暂无封面" : "", FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 7, 0, 0) });
        placeholder.VerticalAlignment = VerticalAlignment.Center; background.Children.Add(placeholder); background.Children.Add(image);
        return new Border { Child = background, CornerRadius = new(8), ClipToBounds = true, BorderBrush = Theme.Edge, BorderThickness = new(1) };
    }
    static BitmapSource? ReadWorldCover(SavedWorld world)
    {
        try
        {
            WorldFiles.NoLinks(world.IconPath);
            if (!File.Exists(world.IconPath) || new FileInfo(world.IconPath).Length > 4 * 1024 * 1024) return null;
            using var stream = File.OpenRead(world.IconPath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0]; int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 64_000_000 || width > height * 16L || height > width * 16L) return null;
            double scale = Math.Min(1, 384d / Math.Max(width, height)); stream.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale)); bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale)); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException) { return null; }
    }
    FrameworkElement CreateWorldCard(SavedWorld world)
    {
        var card = Cols(new(92), new(14), new(1, GridUnitType.Star));
        var cover = new Image { Source = ReadWorldCover(world), Width = 90, Height = 112, Stretch = Stretch.UniformToFill };
        Row(card, WorldCoverFrame(cover, 90, 112, true), 0);
        var heading = new TextBlock { Text = world.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = world.Name };
        var edition = new Border { Background = Theme.Brush(world.Source.Bedrock ? "#263B51" : "#1C3D33"), CornerRadius = new(4), Padding = new(7, 3, 7, 3), Margin = new(0, 0, 7, 0), Child = new TextBlock { Text = world.Source.Bedrock ? "基岩版" : "Java", FontSize = 10, Foreground = Theme.Brush(world.Source.Bedrock ? "#A9D3FC" : "#91E1BF") } };
        var tags = H(edition, new TextBlock { Text = (world.Version.Length > 0 ? world.Version + " · " : "") + world.Mode, FontSize = 11, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center }); tags.Margin = new(0, 5, 0, 4);
        var source = new TextBlock { Text = world.Source.Name, FontSize = 11, Foreground = Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = world.Summary };
        var seed = new TextBlock { Text = "种子  " + world.SeedText, FontSize = 11, Foreground = Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new("Consolas,Microsoft YaHei UI"), ToolTip = world.SeedText, Margin = new(0, 4, 0, 7) };
        var map = Button("查看地图", (_, e) => { e.Handled = true; if (worldOperationCts != null) return; worldList.SelectedItem = world; new WorldMapWindow(world) { Owner = this }.Show(); });
        map.Foreground = Theme.Accent; map.Background = Theme.Brush("#18392F"); map.BorderBrush = Theme.Brush("#2C6652");
        var backup = Button("备份", async (_, e) => { e.Handled = true; if (worldOperationCts != null) return; worldList.SelectedItem = world; await ExportWorldAsync(true); });
        foreach (var button in new[] { map, backup }) { button.FontSize = 11; button.Padding = new(11, 5, 11, 5); }
        Row(card, V(heading, tags, source, seed, H(map, backup)), 0, 2);
        return card;
    }
    sealed class WorldCardConverter(MainWindow owner) : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is SavedWorld world ? owner.CreateWorldCard(world) : "";
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    void CopyWorldSeed(SavedWorld world)
    {
        try { Clipboard.SetText(world.SeedText); worldMessage.Text = "种子已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { worldMessage.Text = "剪贴板正被占用，请稍后重试。"; }
    }
    void OpenWorlds(GameInstance? game = null, BedrockPackage? package = null)
    {
        if (game != null) requestedWorldRoot = Path.Combine(game.GameDirectory, "saves");
        else if (package != null) try { requestedWorldRoot = BedrockInstances.For(package).UserData; } catch (IOException) { }
        Navigate("worlds");
    }
    async Task RefreshWorldsAsync()
    {
        if (worldOperationCts != null) return;
        worldScanCts?.Cancel(); var cts = worldScanCts = new();
        string? selected = requestedWorldRoot ?? (worldSource.SelectedItem as WorldSource)?.Root; requestedWorldRoot = null;
        string? world = (worldList.SelectedItem as SavedWorld)?.Folder; var instances = games.ToArray();
        worldScanning = true; worldList.ItemsSource = null; worldMessage.Text = "正在读取各版本的地图…"; worldCount.Text = "正在加载…"; worldEmpty.Text = "正在读取存档…\n首次扫描可能需要一些时间。"; worldEmpty.Visibility = Visibility.Visible;
        try
        {
            var result = await Task.Run(() => { var sources = WorldLibrary.Sources(settings, instances); return (Sources: sources, Worlds: WorldLibrary.Scan(sources, cts.Token)); }, cts.Token);
            if (cts.IsCancellationRequested) return;
            worldSources = result.Sources; savedWorlds = result.Worlds;
            worldScanning = false;
            worldSource.ItemsSource = new object[] { "全部版本" }.Concat(worldSources.Cast<object>()).ToArray();
            worldSource.SelectedItem = worldSources.FirstOrDefault(s => selected != null && (WorldFiles.Same(s.Root, selected) || s.Root.StartsWith(selected.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) ?? (object)"全部版本";
            FilterWorlds(); worldList.SelectedItem = savedWorlds.FirstOrDefault(w => w.Folder == world) ?? worldList.Items.Cast<SavedWorld>().FirstOrDefault();
            worldMessage.Text = savedWorlds.Count == 0 ? "还没有找到世界。可以导入 ZIP、.mcworld，或在游戏中创建并保存世界后刷新。" : "选择世界查看种子、备份或地图；跨版本复制会保留原存档。";
        }
        catch (OperationCanceledException) { if (ReferenceEquals(worldScanCts, cts)) { worldEmpty.Text = "存档读取已取消\n点击刷新重新读取。"; worldCount.Text = "读取已取消"; } }
        catch (Exception e) { if (ReferenceEquals(worldScanCts, cts)) { worldMessage.Text = "地图列表读取失败：" + e.Message; worldCount.Text = "加载未完成 · 点击刷新重试"; worldEmpty.Text = "暂时无法读取存档\n请检查存档目录后刷新。"; } }
        finally { if (ReferenceEquals(worldScanCts, cts)) { worldScanCts = null; worldScanning = false; } cts.Dispose(); }
    }
    void FilterWorlds()
    {
        if (worldScanning) return;
        string query = worldSearch.Text.Trim(); var source = worldSource.SelectedItem as WorldSource;
        string? selected = (worldList.SelectedItem as SavedWorld)?.Folder;
        var list = savedWorlds.Where(w => (source == null || WorldFiles.Same(w.Source.Root, source.Root)) && (w.Name + " " + w.Summary + " " + w.SeedText).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(w => w.Played).ToList();
        worldList.ItemsSource = list; worldCount.Text = $"{list.Count} 个世界 · " + (source == null ? $"{worldSources.Count} 个存档目录" : source.Label) + " · 最近游玩优先";
        worldEmpty.Text = query.Length > 0 ? "没有找到匹配的世界\n试试世界名称、版本或种子。" : "此处还没有世界\n导入地图，或在游戏中创建并保存世界。";
        worldEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        worldList.SelectedItem = list.FirstOrDefault(w => w.Folder == selected) ?? list.FirstOrDefault();
    }
    void SelectWorld()
    {
        var world = worldList.SelectedItem as SavedWorld; worldActions.IsEnabled = world != null && worldOperationCts == null;
        worldCover.Source = world == null ? null : ReadWorldCover(world); worldCover.Visibility = worldCover.Source == null ? Visibility.Collapsed : Visibility.Visible;
        worldName.Text = world?.Name ?? "选择一个世界"; worldName.ToolTip = world?.Folder; worldSeed.Text = world?.SeedText ?? "";
        worldEdition.Text = world == null ? "地图与存档" : world.Source.Edition + (world.Version.Length > 0 ? " · " + world.Version : "");
        worldDetails.Text = world == null ? "选择左侧世界，查看地图、种子或管理存档。" : world.Source.Name + "\n" + world.Mode + " · 上次游玩 " + world.PlayedText + "\n出生点  X " + world.SpawnX + "  Z " + world.SpawnZ + (world.Warning.Length > 0 ? "\n\n" + world.Warning : "");
        if (worldActions.Children.Count > 1) ((Button)worldActions.Children[1]).IsEnabled = world?.Seed != null;
    }
    WorldSource? ChooseWorldSource(string caption, bool? bedrock = null, WorldSource? exclude = null)
    {
        var choices = worldSources.Where(s => (bedrock == null || s.Bedrock == bedrock) && (exclude == null || !WorldFiles.Same(s.Root, exclude.Root))).ToList();
        if (choices.Count == 0) { worldMessage.Text = "没有可用的目标版本，请先添加或启动相应游戏实例。"; return null; }
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        if (worldSource.SelectedItem is WorldSource selected && choices.Contains(selected)) picker.SelectedItem = selected;
        var dialog = new Window { Title = caption, Width = 520, Height = 290, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Background, Foreground = Theme.Text, FontFamily = FontFamily };
        var confirm = Button(exclude == null ? "导入到所选版本" : "复制并保留原存档", (_, _) => dialog.DialogResult = true, true);
        dialog.Content = new Border { Padding = new(24), Child = V(Text(caption, 21, true), Gap(), picker, Gap(), Hint("跨版本使用地图前请保留备份。旧版本不一定能正确读取新版本的存档；模组地图需要相应模组。"), Gap(), H(confirm, Button("取消", (_, _) => dialog.Close()))) };
        return dialog.ShowDialog() == true ? picker.SelectedItem as WorldSource : null;
    }
    async Task WorldWorkAsync(Func<IProgress<string>, CancellationToken, string> work)
    {
        if (worldOperationCts != null) return;
        if (launchPreparing || jobs.Any(j => !j.Finished)) { worldMessage.Text = "请等待游戏启动或下载、安装任务完成，再管理存档。"; return; }
        var cts = worldOperationCts = new(); worldScanCts?.Cancel(); worldActions.IsEnabled = false; worldCancel.Visibility = Visibility.Visible;
        worldSource.IsEnabled = worldSearch.IsEnabled = worldList.IsEnabled = false; worldMessage.Text = "正在处理存档…";
        try { var progress = new Progress<string>(s => worldMessage.Text = s); string result = await Task.Run(() => work(progress, cts.Token), cts.Token); worldMessage.Text = "已完成 · " + result; }
        catch (OperationCanceledException) { worldMessage.Text = "已取消，原存档保留。"; }
        catch (Exception e) { worldMessage.Text = "操作未完成：" + e.Message; }
        finally { worldOperationCts = null; cts.Dispose(); worldCancel.Visibility = Visibility.Collapsed; worldSource.IsEnabled = worldSearch.IsEnabled = worldList.IsEnabled = true; SelectWorld(); }
    }
    async Task ImportWorldAsync(bool folder = false)
    {
        if (worldOperationCts != null) return;
        string file;
        if (folder) { var dialog = new OpenFolderDialog { Title = "选择包含 level.dat 的世界文件夹" }; if (dialog.ShowDialog(this) != true) return; file = dialog.FolderName; }
        else { var dialog = new OpenFileDialog { Title = "导入单个世界或恢复备份", Filter = "地图 / 备份|*.zip;*.mcworld" }; if (dialog.ShowDialog(this) != true) return; file = dialog.FileName; }
        var target = ChooseWorldSource("选择导入到的版本", file.EndsWith(".mcworld", StringComparison.OrdinalIgnoreCase) ? true : null); if (target == null) return;
        await WorldWorkAsync((p, ct) => folder ? WorldFiles.CopyDirectory(file, target, Path.GetFileName(file), p, ct) : WorldFiles.Import(file, target, p, ct));
        string message = worldMessage.Text; await RefreshWorldsAsync(); worldMessage.Text = message;
    }
    async Task ExportWorldAsync(bool backup)
    {
        if (worldList.SelectedItem is not SavedWorld world) return;
        string extension = world.Source.Bedrock ? ".mcworld" : ".zip", output;
        string name = string.Concat(world.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (backup) { string root = Path.Combine(Settings.DataPath, "world-backups"); Directory.CreateDirectory(root); output = Paths.Safe(root, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + extension); }
        else { var dialog = new SaveFileDialog { Title = "导出地图", FileName = name + extension, Filter = world.Source.Bedrock ? "基岩版地图|*.mcworld" : "世界压缩包|*.zip", OverwritePrompt = false }; if (dialog.ShowDialog(this) != true) return; output = dialog.FileName; }
        await WorldWorkAsync((p, ct) => WorldFiles.Export(world, output, p, ct));
    }
    async Task CopyWorldAsync()
    {
        if (worldList.SelectedItem is not SavedWorld world) return;
        var target = ChooseWorldSource("将“" + world.Name + "”复制到另一个版本", world.Source.Bedrock, world.Source); if (target == null) return;
        await WorldWorkAsync((p, ct) => WorldFiles.Copy(world, target, p, ct)); string message = worldMessage.Text; await RefreshWorldsAsync(); worldMessage.Text = message;
    }
    async Task RecycleWorldAsync()
    {
        if (worldList.SelectedItem is not SavedWorld world || !Confirm("把“" + world.Name + "”移入此版本的回收目录？\n文件会保留在 .blockdock-recycle，可从“导入文件夹”恢复。")) return;
        await WorldWorkAsync((_, ct) => { ct.ThrowIfCancellationRequested(); return WorldFiles.Recycle(world); }); string message = worldMessage.Text; await RefreshWorldsAsync(); worldMessage.Text = message;
    }
}
