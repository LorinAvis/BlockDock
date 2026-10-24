using BlockDock;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class MapInteractionChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Flags)!.GetValue(value)!;
    static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags)!.Invoke(value, args);
    static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags)!.SetValue(value, data);
    static object? Property(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
    static void Check(bool good, string text) { if (!good) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new Exception("Map interaction fixture timed out"); task.GetAwaiter().GetResult();
    }
    static void Snapshot(WorldMapWindow map, string name, int width, int height)
    {
        var content = (FrameworkElement)map.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string root = Path.GetFullPath("artifacts/previews"); Directory.CreateDirectory(root); using var file = File.Create(Path.Combine(root, name + ".png")); png.Save(file);
    }
    public static void Run(WorldMapWindow map)
    {
        var filter = Field<ComboBox>(map, "featureFilter"); filter.SelectedItem = "全部结构";
        var search = Field<TextBox>(map, "featureSearch"); var sort = Field<ComboBox>(map, "featureSort");
        var image = Field<Image>(map, "image"); var structures = Field<ListView>(map, "structures");
        var predicted = Field<SeedMapResult>(map, "predicted");
        if (!predicted.Features.Any(f => f.Name == "末地要塞")) { Field<ComboBox>(map, "range").SelectedItem = 16384; Wait((Task)Call(map, "RenderAsync")!); predicted = Field<SeedMapResult>(map, "predicted"); }
        var foundStronghold = predicted.Features.FirstOrDefault(f => f.Name == "末地要塞");
        Check(foundStronghold != null, "seed map exposes real stronghold predictions in the overworld");
        var stronghold = foundStronghold ?? throw new Exception("Missing stronghold fixture");
        var original = image.Source; var request = Field<object>(map, "rendered"); string caption = Field<TextBlock>(map, "mapTitle").Text;
        Check(map.MinWidth <= 1120 && map.MinHeight <= 820, "map window supports the launcher minimum review size");
        sort.SelectedIndex = 1;
        var ordered = structures.Items.Cast<SeedFeature>().Select(f => f.Name).ToArray();
        Check(ordered.SequenceEqual(ordered.OrderBy(n => n)), "structure list supports name sorting without regenerating terrain");
        search.Text = "末地要塞";
        Check(structures.Items.Count > 0 && structures.Items.Cast<SeedFeature>().All(f => f.Name == "末地要塞"), "structure search filters the generated result rather than starting a new request");
        search.Text = "no-such-structure";
        Check(structures.Items.Count == 0 && ReferenceEquals(original, image.Source), "empty structure search keeps the generated map intact");
        search.Text = ""; sort.SelectedIndex = 0;
        var center = (Point)Call(map, "ViewCenter")!;
        var distances = structures.Items.Cast<SeedFeature>().Select(f => Math.Pow(f.X - center.X, 2) + Math.Pow(f.Z - center.Y, 2)).ToArray();
        Check(distances.SequenceEqual(distances.OrderBy(n => n)), "structure distance order uses the current map viewport center");

        Call(map, "SelectFeature", stronghold, false);
        string expected = $"X: {stronghold.X}, Z: {stronghold.Z}";
        Check(Equals(Property(map, "SelectionCoordinates"), expected) && Field<TextBlock>(map, "selectedCoordinates").Text == expected
            && structures.SelectedItem is SeedFeature selected && selected == stronghold && Field<Button>(map, "copyCoordinates").IsEnabled,
            "map marker selection and list selection share exact signed coordinate details and enable copy");
        Check(Field<TextBlock>(map, "selectionNote").Text.Contains("不是传送门房间"), "stronghold detail distinguishes structure location from the portal-room position");
        var markers = Field<Canvas>(map, "canvas").Children.OfType<Border>().Where(b => b.Tag != null).ToArray();
        Check(markers.Any(b => Property(b.Tag!, "Feature") is SeedFeature feature && feature == stronghold), "selected stronghold remains a real selectable map marker even when markers overlap");
        Set(map, "pointerDown", new Point(10, 10)); Set(map, "panning", true);
        Call(map, "EndPointer", Field<ScrollViewer>(map, "viewport"), new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        Check(Equals(Property(map, "SelectionCoordinates"), expected) && Field<Point?>(map, "pointerDown") == null,
            "releasing a map drag does not accidentally replace the selected structure with a map pin");
        var other = structures.Items.Cast<SeedFeature>().FirstOrDefault(f => f != stronghold);
        if (other != null)
        {
            structures.SelectedItem = other;
            Check(Equals(Property(map, "SelectionCoordinates"), $"X: {other.X}, Z: {other.Z}"), "choosing a structure in the list updates the same coordinate card");
            Call(map, "SelectFeature", stronghold, false);
        }
        Snapshot(map, "seed-map-selected-1120", 1120, 820);
        var content = (FrameworkElement)map.Content; var copy = Field<Button>(map, "copyCoordinates"); var coordinates = Field<TextBlock>(map, "selectedCoordinates");
        var copyAt = copy.TranslatePoint(new Point(), content); var coordsAt = coordinates.TranslatePoint(new Point(), content);
        Check(copyAt.Y >= coordsAt.Y + coordinates.ActualHeight && copyAt.Y + copy.ActualHeight <= content.ActualHeight
            && structures.ActualHeight >= 40 && Field<ScrollViewer>(map, "viewport").ActualWidth >= 600,
            "1120 layout keeps copy directly below coordinates, structure list visible and map dominant");
        Snapshot(map, "seed-map-selected-1460", 1460, 930);
        string worldFolder = Field<SavedWorld>(map, "world").Folder, forbiddenExport = Path.Combine(worldFolder, "map-export.png"); bool blockedExport = false;
        try { Call(map, "ExportImageTo", forbiddenExport); }
        catch (TargetInvocationException e) when (e.InnerException is IOException) { blockedExport = true; }
        Check(blockedExport && !File.Exists(forbiddenExport), "map PNG export cannot overwrite or add files inside the source world");
        string export = Path.GetFullPath("work/world-checks/map-export-" + Guid.NewGuid().ToString("N") + ".png"); Call(map, "ExportImageTo", export);
        Check(File.ReadAllBytes(export).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && !Directory.EnumerateFiles(Path.GetDirectoryName(export)!, ".blockdock-map-*.tmp").Any(),
            "annotated map exports through an owned temporary PNG and leaves no staging file");

        var dimension = Field<ComboBox>(map, "dimension"); var seed = Field<TextBox>(map, "seed"); var algorithm = Field<ComboBox>(map, "algorithm"); var large = Field<CheckBox>(map, "large");
        int oldDimension = dimension.SelectedIndex; string oldSeed = seed.Text; object? oldAlgorithm = algorithm.SelectedItem; bool? oldLarge = large.IsChecked;
        dimension.SelectedIndex = 1; seed.Text = "999"; algorithm.SelectedItem = OfflineSeedMap.Match("1.20.1"); large.IsChecked = !oldLarge;
        Check(ReferenceEquals(original, image.Source) && ReferenceEquals(request, Field<object>(map, "rendered")) && Field<TextBlock>(map, "mapTitle").Text == caption
            && Field<TextBlock>(map, "dirtyHint").Visibility == Visibility.Visible && (int)Property(request, "Dimension")! == oldDimension
            && (long)Property(request, "Seed")! == 262 && Field<TextBlock>(map, "selectedContext").Text.Contains("主世界"),
            "changing seed, dimension, algorithm and world type never relabels old map data or selected coordinates");
        dimension.SelectedIndex = oldDimension; seed.Text = oldSeed; algorithm.SelectedItem = oldAlgorithm; large.IsChecked = oldLarge;
        Check(Field<TextBlock>(map, "dirtyHint").Visibility == Visibility.Collapsed, "restoring the rendered settings removes the stale-parameters hint");

        var showMarkers = Field<CheckBox>(map, "showMarkers"); showMarkers.IsChecked = false;
        Check(!Field<Canvas>(map, "canvas").Children.OfType<Border>().Any(b => b.Tag != null) && ReferenceEquals(original, image.Source), "marker visibility toggles overlays without recomputing or losing map data");
        showMarkers.IsChecked = true; Field<CheckBox>(map, "gridLines").IsChecked = true;
        Check(Field<Canvas>(map, "canvas").Children.OfType<Border>().Any(b => b.Tag != null) && ReferenceEquals(original, image.Source), "showing markers and the coordinate grid preserves the current raster");
        var raster = Field<WorldMapImage>(map, "raster"); int pointX = raster.MinX + raster.Width * raster.BlocksPerPixel / 2 + 13, pointZ = raster.MinZ + raster.Width * raster.BlocksPerPixel / 2 - 21;
        Call(map, "SelectMapPoint", pointX, pointZ, "地图位置");
        Check(Equals(Property(map, "SelectionCoordinates"), $"X: {pointX}, Z: {pointZ}") && structures.SelectedItem == null && copy.IsEnabled, "a free map point can be selected and copied independently of predicted structures");
        Call(map, "ClearSelection");
        Check(Equals(Property(map, "SelectionCoordinates"), "") && !copy.IsEnabled && structures.SelectedItem == null, "clearing selection removes coordinate copy state and list highlighting");

        seed.Text = "not-an-integer"; Wait((Task)Call(map, "RenderAsync")!);
        Check(ReferenceEquals(original, image.Source) && Field<TextBlock>(map, "message").Text.Contains("整数种子") && Field<TextBlock>(map, "dirtyHint").Visibility == Visibility.Visible,
            "invalid seed is reported without passing it to native code or presenting old data as new");
        seed.Text = oldSeed;
        var pendingTask = (Task)Call(map, "RenderAsync")!; var cancellation = Field<CancellationTokenSource?>(map, "pending");
        Check(cancellation != null, "a running prediction exposes a cancellation token while native work is pending"); cancellation!.Cancel(); Wait(pendingTask);
        Check(image.Source == null && Field<object?>(map, "predicted") == null && Field<object?>(map, "rendered") == null && Field<TextBlock>(map, "message").Text.Contains("取消") && Field<Button>(map, "render").IsEnabled,
            "cancelled map generation clears partial results and allows another request");
        Field<TextBox>(map, "x").Text = "30000001"; Wait((Task)Call(map, "RenderAsync")!);
        Check(image.Source == null && Field<object?>(map, "rendered") == null && Field<TextBlock>(map, "message").Text.Contains("未完成"), "render failures cannot leave exportable partial results or stale request metadata");
        Console.WriteLine("MAP INTERACTION CHECKS PASSED; no clipboard writes or real worlds");
    }
}
