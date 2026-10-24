using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;

// Uses the player's installed game textures in memory; no Minecraft artwork is distributed.
public sealed class StructureMapIcons(SavedWorld? world)
{
    readonly ConcurrentDictionary<string, ImageSource> images = new(StringComparer.Ordinal);
    readonly SemaphoreSlim loading = new(1, 1);
    bool loaded;
    static readonly string[] Names = { "村庄", "沙漠神殿", "丛林神庙", "沼泽小屋", "雪屋", "海底神殿", "林地府邸", "掠夺者前哨站", "废弃传送门", "远古城市", "埋藏的宝藏", "试炼密室", "下界要塞", "堡垒遗迹", "末地城", "下界废弃传送门", "末地要塞" };

    // Call once alongside map generation. Get itself never opens files on the UI thread.
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await loading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            await Task.Run(() => Load(ct), ct).ConfigureAwait(false); loaded = true;
        }
        finally { loading.Release(); }
    }
    public ImageSource? Get(string featureName) => images.GetValueOrDefault(featureName);

    void Load(CancellationToken ct)
    {
        foreach (string jar in CandidateJars(ct).Take(32))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(jar).Length > 1024L * 1024 * 1024) continue;
                WorldFiles.NoLinks(jar); using var zip = ZipFile.OpenRead(jar);
                if (zip.Entries.Count > 200_000) continue;
                foreach (string name in Names)
                {
                    ct.ThrowIfCancellationRequested(); if (images.ContainsKey(name)) continue;
                    try { if (Icon(zip, name) is { } icon) images[name] = icon; }
                    catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException) { }
                }
                if (images.Count == Names.Length) return;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    IEnumerable<string> CandidateJars(CancellationToken ct)
    {
        var roots = new List<string>(); var candidates = new List<string>();
        void AddRoot(string root)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(Path.Combine(root, "versions")) && !roots.Any(r => WorldFiles.Same(r, root))) roots.Add(Path.GetFullPath(root));
        }
        string? gameDirectory = world?.Source.Bedrock == false ? Path.GetDirectoryName(world.Source.Root) : null;
        for (string? parent = gameDirectory; parent != null && roots.Count == 0; parent = Path.GetDirectoryName(parent)) AddRoot(parent);
        try { AddRoot(Settings.Load().JavaRoot); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        void AddVersion(string root, string? version)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(version) && seen.Add(version) && seen.Count <= 16)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string id = Paths.Id(version), folder = Paths.Safe(root, "versions/" + id), jar = Path.Combine(folder, id + ".jar");
                    if (File.Exists(jar)) candidates.Add(jar);
                    string metadata = Path.Combine(folder, id + ".json");
                    if (!File.Exists(metadata) || new FileInfo(metadata).Length > 2 * 1024 * 1024) break;
                    WorldFiles.NoLinks(metadata); var json = JsonNode.Parse(File.ReadAllText(metadata));
                    version = json?["jar"]?.ToString() ?? json?["inheritsFrom"]?.ToString();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { break; }
            }
        }
        foreach (string root in roots)
        {
            AddVersion(root, world?.Version);
            if (gameDirectory != null && Path.GetDirectoryName(gameDirectory) is { } parent && WorldFiles.Same(parent, Path.Combine(root, "versions"))) AddVersion(root, Path.GetFileName(gameDirectory));
            AddVersion(root, world?.Source.Version);
        }
        // Only direct version folders are considered; never recurse into saves, mods or libraries.
        foreach (string root in roots)
            foreach (string folder in WorldFiles.Directories(Path.Combine(root, "versions")).Take(256).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested(); string jar = Path.Combine(folder, Path.GetFileName(folder) + ".jar"); if (File.Exists(jar)) candidates.Add(jar);
            }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static BitmapSource? Texture(ZipArchive zip, params string[] paths)
    {
        foreach (string path in paths)
        {
            var entry = zip.GetEntry("assets/minecraft/textures/" + path + ".png");
            if (entry == null || entry.Length is <= 0 or > 4 * 1024 * 1024) continue;
            using var input = entry.Open(); var bytes = new byte[(int)entry.Length]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) continue;
            int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
            if (width is < 1 or > 2048 || height is < 1 or > 8192 || (long)width * height > 4_194_304) continue;
            using var data = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0]; if (frame.PixelWidth is < 1 or > 2048 || frame.PixelHeight is < 1 or > 8192 || (long)frame.PixelWidth * frame.PixelHeight > 4_194_304) continue;
            frame.Freeze(); return frame;
        }
        return null;
    }
    static BitmapSource Crop(BitmapSource source, int x, int y, int width, int height, int atlasWidth, int atlasHeight)
    {
        if (source.PixelWidth % atlasWidth != 0 || source.PixelHeight % atlasHeight != 0) throw new InvalidDataException("Unsupported entity texture layout.");
        int sx = source.PixelWidth / atlasWidth, sy = source.PixelHeight / atlasHeight;
        var result = new CroppedBitmap(source, new Int32Rect(x * sx, y * sy, width * sx, height * sy)); result.Freeze(); return result;
    }
    static ImageSource Drawing(double width, double height, Action<DrawingContext> draw)
    {
        var group = new DrawingGroup(); RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var dc = group.Open()) { dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); draw(dc); }
        group.Freeze(); var image = new DrawingImage(group); image.Freeze(); return image;
    }
    static ImageSource Head(BitmapSource texture, string name)
    {
        if (name == "远古城市")
            return Drawing(26, 18, dc =>
            {
                var tendril = Crop(texture, 52, 32, 16, 16, 128, 128);
                dc.DrawImage(tendril, new Rect(0, 0, 10, 16));
                dc.PushTransform(new ScaleTransform(-1, 1, 13, 0)); dc.DrawImage(tendril, new Rect(0, 0, 10, 16)); dc.Pop();
                dc.DrawImage(Crop(texture, 10, 42, 16, 16, 128, 128), new Rect(5, 2, 16, 16));
            });
        if (name == "堡垒遗迹")
            return Drawing(14, 10, dc =>
            {
                dc.DrawImage(Crop(texture, 52, 10, 1, 5, 64, 64), new Rect(0, 1, 2, 5));
                dc.DrawImage(Crop(texture, 52, 10, 1, 5, 64, 64), new Rect(12, 1, 2, 5));
                dc.DrawImage(Crop(texture, 8, 8, 10, 8, 64, 64), new Rect(2, 1, 10, 8));
                dc.DrawImage(Crop(texture, 32, 2, 4, 4, 64, 64), new Rect(5, 5, 4, 4));
                var tusk = Crop(texture, 3, 5, 1, 2, 64, 64); dc.DrawImage(tusk, new Rect(4, 5, 1, 2)); dc.DrawImage(tusk, new Rect(9, 5, 1, 2));
            });
        bool witch = name == "沼泽小屋"; int atlasHeight = witch ? 128 : 64, offset = witch ? 10 : 0;
        return Drawing(witch ? 12 : 8, 11 + offset, dc =>
        {
            int left = witch ? 2 : 0;
            dc.DrawImage(Crop(texture, 8, 8, 8, 10, 64, atlasHeight), new Rect(left, offset, 8, 10));
            dc.DrawImage(Crop(texture, 26, 2, 2, 4, 64, atlasHeight), new Rect(left + 3, offset + 7, 2, 4));
            if (witch)
            {
                dc.DrawImage(Crop(texture, 10, 74, 10, 2, 64, 128), new Rect(1, 9, 10, 2));
                dc.DrawImage(Crop(texture, 7, 83, 7, 4, 64, 128), new Rect(2, 5, 7, 4));
                dc.DrawImage(Crop(texture, 4, 91, 4, 4, 64, 128), new Rect(3, 1, 4, 4));
            }
        });
    }
    static ImageSource Cube(BitmapSource side, BitmapSource? top = null, int blockHeight = 16)
    {
        BitmapSource Frame(BitmapSource frame) { int size = frame.PixelWidth; if (frame.PixelHeight < size) throw new InvalidDataException(); var crop = new CroppedBitmap(frame, new Int32Rect(0, 0, size, size)); crop.Freeze(); return crop; }
        var front = Frame(side); var ceiling = Frame(top ?? side);
        if (blockHeight < 16) { int height = front.PixelWidth * blockHeight / 16; var crop = new CroppedBitmap(front, new Int32Rect(0, front.PixelHeight - height, front.PixelWidth, height)); crop.Freeze(); front = crop; }
        return Drawing(32, 16 + blockHeight * 1.125, dc =>
        {
            void Face(BitmapSource texture, Matrix transform, byte shade, int height = 16)
            {
                dc.PushTransform(new MatrixTransform(transform)); dc.DrawImage(texture, new Rect(0, 0, 16, height));
                if (shade > 0) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(shade, 0, 0, 0)), null, new Rect(0, 0, 16, height)); dc.Pop();
            }
            Face(front, new Matrix(1, .5, 0, 1.125, 0, 8), 30, blockHeight);
            Face(front, new Matrix(1, -.5, 0, 1.125, 16, 16), 65, blockHeight);
            Face(ceiling, new Matrix(1, -.5, 1, .5, 0, 8), 0);
        });
    }
    static ImageSource? Icon(ZipArchive zip, string name)
    {
        if (name == "末地要塞")
        {
            var side = Texture(zip, "block/end_portal_frame_side", "blocks/endframe_side", "blocks/end_portal_frame_side");
            var frameTop = Texture(zip, "block/end_portal_frame_top", "blocks/endframe_top", "blocks/end_portal_frame_top");
            return side == null || frameTop == null ? null : Cube(side, frameTop, 13);
        }
        string? entity = name switch { "村庄" => "villager/villager", "沼泽小屋" => "witch", "林地府邸" => "illager/vindicator", "掠夺者前哨站" => "illager/pillager", "远古城市" => "warden/warden", "堡垒遗迹" => "piglin/piglin", _ => null };
        if (entity != null)
        {
            var face = Texture(zip, "entity/" + entity, name == "村庄" ? "entity/villager/farmer" : "entity/" + entity);
            return face == null ? null : Head(face, name);
        }
        if (name == "埋藏的宝藏")
        {
            var chest = Texture(zip, "entity/chest/normal"); if (chest == null) return null;
            return Drawing(14, 14, dc =>
            {
                dc.DrawImage(Crop(chest, 14, 33, 14, 10, 64, 64), new Rect(0, 4, 14, 10));
                dc.DrawImage(Crop(chest, 14, 14, 14, 5, 64, 64), new Rect(0, 0, 14, 5));
                dc.DrawImage(Crop(chest, 1, 1, 2, 4, 64, 64), new Rect(6, 3, 2, 4));
            });
        }
        string? block = name switch { "沙漠神殿" => "sandstone", "丛林神庙" => "mossy_cobblestone", "雪屋" => "snow", "海底神殿" => "sea_lantern", "废弃传送门" or "下界废弃传送门" => "obsidian", "试炼密室" => "copper_block", "下界要塞" => "nether_bricks", "末地城" => "purpur_block", _ => null };
        if (block == null) return null;
        string legacy = block switch { "mossy_cobblestone" => "cobblestone_mossy", "nether_bricks" => "nether_brick", "sandstone" => "sandstone_normal", _ => block };
        var texture = Texture(zip, "block/" + block, "blocks/" + legacy); if (texture == null) return null;
        var top = block == "sandstone" ? Texture(zip, "block/sandstone_top", "blocks/sandstone_top") : null; return Cube(texture, top);
    }
}
