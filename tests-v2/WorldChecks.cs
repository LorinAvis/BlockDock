using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class WorldChecks
{
    static void Check(bool good, string text) { if (!good) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, label); }
    static Dictionary<string, string> Hashes(string folder) => WorldFiles.Tree(folder, default).ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
    static int Color(WorldMapImage image, int x, int z) { int i = ((z - image.MinZ) / image.BlocksPerPixel * image.Width + (x - image.MinX) / image.BlocksPerPixel) * 4; return image.Pixels[i + 2] << 16 | image.Pixels[i + 1] << 8 | image.Pixels[i]; }
    public static void Run()
    {
        string root = Path.GetFullPath("work/world-checks/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var java = new WorldSource("1.20.1 Fabric", "1.20.1", false, Path.Combine(root, "java/saves"));
        var bedrock = new WorldSource("1.18.2 UWP", "1.18.2", true, Path.Combine(root, "bedrock/minecraftWorlds"));
        var j = WorldFixtures.World(java, "Cherry Harbor", "樱花港口🌸", long.MinValue);
        var b = WorldFixtures.World(bedrock, "Island", "基岩岛屿", long.MaxValue);
        Check(j.Seed == long.MinValue && j.Name == "樱花港口🌸" && j.Version == "1.20.1" && j.Played?.ToUnixTimeSeconds() == 1_700_000_000,
            "Java gzip NBT preserves signed 64-bit seed, Chinese and modified-UTF8 emoji");
        Check(b.Seed == long.MaxValue && b.Version == "1.18.2" && b.Played?.ToUnixTimeSeconds() == 1_700_000_000,
            "Bedrock little-endian header, version list and seconds timestamp are read correctly");
        Check(WorldLibrary.Scan(new[] { java, bedrock }, default).Count == 2, "world list combines versions and editions without changing original folders");
        File.Copy(Path.Combine(j.Folder, "level.dat"), Path.Combine(j.Folder, "level.dat_old")); File.WriteAllText(Path.Combine(j.Folder, "level.dat"), "broken");
        Check(WorldLibrary.Read(java, j.Folder) is { Seed: long.MinValue, Warning.Length: > 0 }, "damaged metadata falls back to old file and clearly reports it"); File.Copy(Path.Combine(j.Folder, "level.dat_old"), Path.Combine(j.Folder, "level.dat"), true);
        var before = Hashes(j.Folder); var target = java with { Name = "1.21.1", Version = "1.21.1", Root = Path.Combine(root, "target/saves") };
        string copy = WorldFiles.Copy(j, target, null, default);
        Check(Directory.Exists(copy) && WorldLibrary.Read(target, copy).Seed == j.Seed && Same(before, Hashes(j.Folder)), "copy to another version verifies files and preserves original world");
        string again = WorldFiles.Copy(j, target, null, default); Check(copy != again && Directory.Exists(copy), "copy never overwrites existing world with the same name");
        Reject(() => WorldFiles.Copy(j, bedrock, null, default), "Java worlds cannot be copied into Bedrock without conversion");
        Reject(() => WorldFiles.Copy(j, java, null, default), "same-directory copy is rejected");
        Reject(() => WorldFiles.CopyDirectory(j.Folder, java with { Root = Path.Combine(j.Folder, "nested/saves") }, "bad", null, default), "copy cannot recursively import into its own child directory");
        string zip = Path.Combine(root, "backup.zip"); WorldFiles.Export(j, zip, null, default);
        string restored = WorldFiles.Import(zip, target, null, default);
        Check(WorldLibrary.Read(target, restored).Seed == j.Seed && Same(before, Hashes(j.Folder)), "backup ZIP round trip restores independent usable world");
        string multiple = Path.Combine(root,"multiple.zip"); using(var archive=ZipFile.Open(multiple,ZipArchiveMode.Create)) { archive.CreateEntryFromFile(Path.Combine(j.Folder,"level.dat"),"one/level.dat");archive.CreateEntryFromFile(Path.Combine(j.Folder,"level.dat"),"two/level.dat"); }
        Reject(()=>WorldFiles.Import(multiple,target,null,default),"archive containing two worlds is rejected without choosing silently");
        using(var interrupted=new CancellationTokenSource())
        {
            using var input=new InterruptStream(new byte[1024*1024],interrupted);using var output=new MemoryStream();bool stopped=false;
            try { typeof(WorldFiles).GetMethod("Transfer",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{input,output,1024L*1024,"fixture",null,interrupted.Token,null}); }
            catch(TargetInvocationException e) when(e.InnerException is OperationCanceledException) { stopped=true; }
            Check(stopped&&output.Length==131072,"large-file transfer can cancel after one buffer, before completing the file");
        }
        Reject(() => WorldFiles.Export(j, Path.Combine(j.Folder, "nested.zip"), null, default), "backup cannot modify source world by writing an archive inside it");
        using (var locked = new FileStream(Path.Combine(j.Folder, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Reject(() => WorldFiles.Export(j, Path.Combine(root, "locked.zip"), null, default), "world currently in use is refused before copying any data");
        foreach (string entry in new[] { "../outside.txt", "C:/outside.txt", "safe/../../outside.txt", "NUL.txt", "folder/CON", "level.dat:stream" })
        {
            string badZip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"); using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create)) { using var output = new StreamWriter(archive.CreateEntry(entry).Open()); output.Write("fixture"); }
            Reject(() => WorldFiles.Import(badZip, target, null, default), "reject unsafe archive entry: " + entry);
        }
        Check(!Directory.EnumerateDirectories(target.Root).Any(p => Path.GetFileName(p).StartsWith(".blockdock-")), "failed imports clean only their own staging folders");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false; try { WorldFiles.Copy(j, target, null, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && Same(before, Hashes(j.Folder)), "cancelled world copy preserves source");
        }
        string recycled = WorldFiles.Recycle(WorldLibrary.Read(target, copy));
        Check(!Directory.Exists(copy) && File.Exists(Path.Combine(recycled, "level.dat")), "recycle keeps all files in the same version's recovery directory");
        Check(!WorldLibrary.Scan(new[] { target }, default).Any(w => w.Folder == recycled), "recycled worlds do not reappear in active list");
        string recovered = WorldFiles.CopyDirectory(recycled, target, "Recovered", null, default); Check(File.Exists(Path.Combine(recovered, "level.dat")), "recycled world can be restored through folder import");
        string linked = Path.Combine(root, "linked"); DirectoryJunction.Create(linked, j.Folder);
        Reject(() => WorldFiles.CopyDirectory(linked, target, "linked", null, default), "directory links are not followed by world import");
        WorldFixtures.Demo(j); before = Hashes(j.Folder);
        var actual = WorldMapRenderer.Render(j, 0, 0, 256, 0, null, default);
        Check(actual.Chunks == 256 && actual.Unreadable == 0 && Color(actual, 25, 25) == WorldMapRenderer.Color("minecraft:oak_planks"), "local Java map reads actual paletted chunks and player-built roof blocks");
        Check(Same(before, Hashes(j.Folder)), "Java map preview leaves every original world file unchanged");
        foreach (bool dense in new[] { true, false })
        {
            var p = WorldFixtures.World(java, "packed-" + dense, "Packed", 0); WorldFixtures.Region(Path.Combine(p.Folder, "region"), 0, 0, new[] { (0, 0, WorldFixtures.Chunk(0, 0, false, dense)) });
            var image = WorldMapRenderer.Render(p, 0, 0, 256, 0, null, default);
            Check(image.Chunks == 1 && image.Unreadable == 0 && Color(image, 13, 0) == WorldMapRenderer.Color("minecraft:oak_planks"), dense ? "pre-1.16 dense five-bit block state packing crosses long boundaries correctly" : "modern padded five-bit block state packing is read correctly");
        }
        WorldFixtures.Database(b, WorldFixtures.BedrockSection()); before = Hashes(b.Folder);
        var bedMap = WorldMapRenderer.Render(b, 0, 0, 256, 0, null, default);
        Check(bedMap.Chunks == 1 && bedMap.Unreadable == 0 && Color(bedMap, 8, 8) == WorldMapRenderer.Color("minecraft:oak_planks"), "Bedrock reader uses active SST files and newest WAL sequence, including tombstones");
        Check(Same(before, Hashes(b.Folder)), "Bedrock preview does not create or repair database files");
        string mcworld = Path.Combine(root, "island.mcworld"); WorldFiles.Export(b, mcworld, null, default);
        Reject(() => WorldFiles.Import(mcworld, target, null, default), "Bedrock header cannot masquerade as a Java root compound during ZIP import");
        string bedCopy = WorldFiles.Import(mcworld, bedrock with { Root = Path.Combine(root, "bedrock-target/minecraftWorlds") }, null, default);
        Check(File.Exists(Path.Combine(bedCopy, "db/CURRENT")) && File.Exists(Path.Combine(bedCopy, "level.dat")), "Bedrock mcworld export/import retains database and little-endian world metadata");
        foreach (int bits in new[] { 0, 3, 5 })
        {
            var world = WorldFixtures.World(bedrock, "bits" + bits, "Bedrock Packed", 1); WorldFixtures.Database(world, WorldFixtures.BedrockSection("grass_block", bits), false);
            var image = WorldMapRenderer.Render(world, 0, 0, 256, 0, null, default);
            Check(image.Chunks == 1 && image.Unreadable == 0 && Color(image, 13, 0) == WorldMapRenderer.Color("minecraft:grass_block"), "Bedrock palette and 32-bit packing: " + bits + " bits");
        }
        byte[] log = Enumerable.Range(0, 90000).Select(n => (byte)(n % 251)).ToArray(); Check(BedrockWorldReader.LogRecords(WorldFixtures.Physical(log)).Single().SequenceEqual(log), "fragmented LevelDB records across 32KB blocks are reconstructed");
        Check(Encoding.ASCII.GetString(BedrockWorldReader.Snappy(new byte[] { 10, 16, 104, 101, 108, 108, 111, 18, 5, 0 })) == "hellohello", "Snappy overlapping copy decoding");
        uint masked = BedrockWorldReader.MaskedCrc(Encoding.ASCII.GetBytes("123456789")); uint unmasked = masked - 0xa282ead8U; unmasked = (unmasked >> 17) | (unmasked << 15); Check(unmasked == 0xe3069283, "CRC32C validates against standard test vector");
        using (var db = File.Open(Path.Combine(b.Folder, "db/000001.ldb"), FileMode.Open, FileAccess.Write)) { db.Position = 5; db.WriteByte(98); }
        Reject(() => WorldMapRenderer.Render(b, 0, 0, 256, 0, null, default), "damaged database block is refused instead of silently drawing stale data");
        WorldFixtures.Database(b, WorldFixtures.BedrockSection());
        var algorithm = OfflineSeedMap.Match("1.18.2")!;
        Check(OfflineSeedMap.BiomeAt(algorithm, 262, 0, 0) == 14, "offline native generator matches upstream known seed 262 mushroom biome vector");
        Check(OfflineSeedMap.Match("26.3") == null && OfflineSeedMap.Match("1.21.11") == null && OfflineSeedMap.Match("26w01a") == null && OfflineSeedMap.Match("1.20.99")==null && OfflineSeedMap.Match("1.20.1.0")==null && OfflineSeedMap.Match("1.20.6")!=null, "unsupported release families, patches and snapshots are never guessed as another version");
        var predicted = OfflineSeedMap.Render(false, algorithm, 262, 0, 0, 1024, 0, false, null, default);
        var repeated = OfflineSeedMap.Render(false, algorithm, 262, 0, 0, 1024, 0, false, null, default);
        Check(predicted.Image.Pixels.SequenceEqual(repeated.Image.Pixels) && predicted.Biomes.Distinct().Count() > 1 && predicted.Features.All(f => f.X >= predicted.Image.MinX && f.X < predicted.Image.MinX + 1024), "offline biome and structure map is deterministic and bounded");
        Reject(() => OfflineSeedMap.Render(true, algorithm, 262, 0, 0, 1024, 0, false, null, default), "Bedrock prediction never substitutes the Java algorithm");
        WorldTextureChecks.Run();
        StrongholdChecks.Run();
        Ui(j, b, actual);
        Console.WriteLine("WORLD CHECKS PASSED; isolated fixtures only");
    }
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Flags)!.GetValue(o)!;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Flags)!.SetValue(o, value);
    static object? Call(object o, string method, params object[] args) => o.GetType().GetMethod(method, Flags)!.Invoke(o, args);
    static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60); while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new Exception("UI fixture timed out"); task.GetAwaiter().GetResult();
    }
    static void Snapshot(FrameworkElement visual, string name, int width, int height)
    {
        visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string root = Path.GetFullPath("artifacts/previews"); Directory.CreateDirectory(root); using var file = File.Create(Path.Combine(root, name + ".png")); png.Save(file);
    }
    static void Ui(SavedWorld java, SavedWorld bedrock, WorldMapImage actual)
    {
        var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new BlockDock.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var cover=new PngBitmapEncoder();cover.Frames.Add(BitmapFrame.Create(BitmapSource.Create(actual.Width,actual.Width,96,96,PixelFormats.Bgra32,null,actual.Pixels,actual.Width*4)));using(var file=File.Create(java.IconPath))cover.Save(file);
        new Settings { CheckUpdatesOnStartup = false, JavaRoot = Environment.GetEnvironmentVariable("BLOCKDOCK_MAP_TEXTURE_ROOT") ?? Path.GetFullPath("work/world-checks/ui-java") }.Save(); var main = new MainWindow();
        try
        {
            var worlds = new List<SavedWorld> { java, bedrock }; Set(main, "worldSources", new List<WorldSource> { java.Source, bedrock.Source }); Set(main, "savedWorlds", worlds);
            var picker = Field<ComboBox>(main, "worldSource"); picker.ItemsSource = new object[] { "全部版本", java.Source, bedrock.Source }; picker.SelectedIndex = 0;
            Call(main, "FilterWorlds"); var pages = Field<Dictionary<string, FrameworkElement>>(main, "pages"); foreach (var page in pages) page.Value.Visibility = page.Key == "worlds" ? Visibility.Visible : Visibility.Collapsed;
            Field<TextBlock>(main, "title").Text = "地图与存档"; Field<TextBlock>(main, "subtitle").Text = "管理各版本的世界，查看种子与本地地图。";
            var nav = Field<Dictionary<string,Button>>(main,"nav"); nav["home"].Background = Brushes.Transparent; nav["home"].Foreground = Theme.Text; nav["worlds"].Background = Theme.Brush("#15392E"); nav["worlds"].Foreground = Theme.Accent;
            Check(Field<ListView>(main, "worldList").Items.Count == 2 && Field<TextBox>(main, "worldSeed").Text == long.MinValue.ToString(), "map management exposes both editions and exact seed without floating point conversion");
            Field<ComboBox>(main,"homeGame").ItemsSource=Array.Empty<GameInstance>();
            Snapshot((FrameworkElement)main.Content, "worlds-1460", 1460, 930); Snapshot((FrameworkElement)main.Content, "worlds-1120", 1120, 780);
            picker.SelectedItem = bedrock.Source; Check(Field<ListView>(main, "worldList").Items.Count == 1, "world version filter refreshes results immediately");
            var map = new WorldMapWindow(java);
            try
            {
                Snapshot((FrameworkElement)map.Content, "map-loading", 1120, 820); Field<ComboBox>(map, "range").SelectedItem = 256;
                Wait((Task)Call(map, "RenderAsync")!); Check(Field<Image>(map, "image").Source != null, "local map viewport renders with coordinates and cancellation controls");
                Snapshot((FrameworkElement)map.Content, "local-map-1120", 1120, 820);
                Field<ComboBox>(map, "mode").SelectedIndex = 1; Field<TextBox>(map, "seed").Text = "262"; Field<ComboBox>(map, "algorithm").SelectedItem = OfflineSeedMap.Match("1.18.2");
                Wait((Task)Call(map, "RenderAsync")!); Check(Field<Image>(map, "image").Source != null, "seed prediction is generated inside launcher without web view or network"); Snapshot((FrameworkElement)map.Content, "seed-map-1120", 1120, 820);
                var original=Field<Image>(map,"image").Source;Field<CheckBox>(map,"gridLines").IsChecked=true;var filter=Field<ComboBox>(map,"featureFilter");filter.SelectedItem="村庄";
                Check(ReferenceEquals(original,Field<Image>(map,"image").Source)&&Field<ListView>(map,"structures").Items.Cast<SeedFeature>().All(f=>f.Name=="村庄"),"structure filtering and grid overlays preserve generated map without recomputing");
                Check(!filter.Items.Cast<string>().Contains("古迹废墟"),"trail ruins are omitted from map markers and filters as requested");
                MapInteractionChecks.Run(map);
            }
            finally { map.Close(); }
            var pe = new WorldMapWindow(bedrock);
            try { Field<ComboBox>(pe, "mode").SelectedIndex = 1; Check(!Field<Button>(pe, "render").IsEnabled && Field<TextBlock>(pe, "explanation").Text.Contains("尚未适配"), "unsupported Bedrock prediction is explicit and cannot accidentally invoke Java rendering"); }
            finally { pe.Close(); }
        }
        finally { main.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
    sealed class InterruptStream(byte[] bytes,CancellationTokenSource cancel):MemoryStream(bytes)
    {
        int reads; public override int Read(byte[] buffer,int offset,int count){int result=base.Read(buffer,offset,count);if(++reads==2)cancel.Cancel();return result;}
    }
}
