using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockDock;

// Opens the selected installed game in read-only mode. Texture pixels remain in memory;
// neither game assets nor a generated texture cache are copied into BlockDock's files.
public sealed class WorldMapTextures : IDisposable
{
    readonly ZipArchive archive;
    readonly Dictionary<string, Tile?> tiles = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonNode?> models = new(StringComparer.Ordinal);
    public string Version { get; }
    WorldMapTextures(ZipArchive archive, string version) { this.archive = archive; Version = version; }
    public sealed class Tile
    {
        public readonly byte[] Pixels;
        public readonly int Tint; // 0 = original RGB; 1 = grass; 2 = foliage; 3 = water.
        public readonly double Average;
        public readonly bool Transparent;
        public Tile(byte[] pixels, int tint) { Pixels = pixels; Tint = tint; Average = Enumerable.Range(0, 16).Average(i => (pixels[i * 4] + pixels[i * 4 + 1] + pixels[i * 4 + 2]) / 3.0); Transparent = Enumerable.Range(0, 16).Any(i => pixels[i * 4 + 3] < 250); }
    }
    public static WorldMapTextures? Open(SavedWorld world, CancellationToken ct)
    {
        if (world.Source.Bedrock) return null;
        foreach (string jar in CandidateJars(world, ct).Take(16))
        {
            ct.ThrowIfCancellationRequested();
            FileStream? input = null; ZipArchive? zip = null;
            try
            {
                if (new FileInfo(jar).Length is <= 0 or > 1024L * 1024 * 1024) continue;
                WorldFiles.NoLinks(jar);
                input = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); zip = new ZipArchive(input, ZipArchiveMode.Read);
                if (zip.Entries.Count > 200_000) continue;
                // Loader-only jars should not mask the inherited vanilla game's assets.
                if (zip.GetEntry("assets/minecraft/textures/block/stone.png") == null && zip.GetEntry("assets/minecraft/textures/blocks/stone.png") == null) continue;
                var result = new WorldMapTextures(zip, Path.GetFileNameWithoutExtension(jar)); zip = null; input = null; return result;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { }
            finally { zip?.Dispose(); input?.Dispose(); }
        }
        return null;
    }
    static IEnumerable<string> CandidateJars(SavedWorld world, CancellationToken ct)
    {
        var roots = new List<string>(); var candidates = new List<string>();
        void AddRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(Path.Combine(root, "versions")) && !roots.Any(r => WorldFiles.Same(r, root))) roots.Add(Path.GetFullPath(root));
        }
        string? directory = Path.GetDirectoryName(world.Source.Root);
        for (int depth = 0; directory != null && depth < 8; depth++, directory = Path.GetDirectoryName(directory)) AddRoot(directory);
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
            // Exact stored version first, then the owning isolated instance and its parent chain.
            AddVersion(root, world.Version);
            string? game = Path.GetDirectoryName(world.Source.Root);
            if (game != null && Path.GetDirectoryName(game) is { } parent && WorldFiles.Same(parent, Path.Combine(root, "versions"))) AddVersion(root, Path.GetFileName(game));
            AddVersion(root, world.Source.Version);
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }
    public Tile? Get(string block)
    {
        if (tiles.TryGetValue(block, out var cached)) return cached;
        if (tiles.Count >= 4096) return null;
        Tile? tile = null;
        try
        {
            var split = block.Split(':'); string space = split.Length == 2 ? split[0] : "minecraft", name = split[^1];
            if (!Resource(space) || !Resource(name)) return null;
            string? reference = ModelTop(space, name);
            var paths = new List<string>();
            if (reference != null) paths.Add(TexturePath(reference));
            string top = name switch { "grass_block" => "grass_block_top", "dirt_path" => "dirt_path_top", "mycelium" => "mycelium_top", "podzol" => "podzol_top", "farmland" => "farmland", "water" or "flowing_water" or "kelp" or "seagrass" => "water_still", "lava" or "flowing_lava" => "lava_still", _ => name };
            paths.Add($"assets/{space}/textures/block/{top}.png");
            paths.Add($"assets/{space}/textures/block/{name}_top.png");
            paths.Add($"assets/{space}/textures/block/{name}.png");
            string legacy = Legacy(top);
            paths.Add($"assets/{space}/textures/blocks/{legacy}.png");
            foreach (string path in paths.Distinct(StringComparer.Ordinal))
                if (ReadTile(path) is { } pixels)
                {
                    int tint = name is "grass_block" or "grass" or "short_grass" or "tall_grass" or "fern" or "large_fern" ? 1
                        : name.Contains("leaves") && !name.Contains("cherry") && !name.Contains("azalea") ? 2
                        : name is "water" or "flowing_water" or "kelp" or "seagrass" ? 3 : 0;
                    tile = new(pixels, tint); break;
                }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException or System.Text.Json.JsonException or InvalidOperationException) { }
        tiles[block] = tile; return tile;
    }
    static bool Resource(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/') && !value.StartsWith('/') && !value.Split('/').Any(s => s is "" or "." or "..");
    static string TexturePath(string value)
    {
        var split = value.Split(':'); string space = split.Length == 2 ? split[0] : "minecraft", path = split[^1];
        if (!Resource(space) || !Resource(path)) throw new InvalidDataException();
        return $"assets/{space}/textures/{path}.png";
    }
    JsonNode? Json(string path)
    {
        if (models.TryGetValue(path, out var cached)) return cached;
        JsonNode? json = null;
        if (models.Count < 8192 && archive.GetEntry(path) is { Length: > 0 and <= 1024 * 1024 } entry)
        {
            using var input = entry.Open(); byte[] bytes = WorldNbt.ReadLimited(input, 1024 * 1024);
            if (bytes.LongLength != entry.Length) throw new InvalidDataException("模型数据长度与归档记录不符。");
            json = JsonNode.Parse(bytes, null, new() { MaxDepth = 48 });
        }
        models[path] = json; return json;
    }
    string? ModelTop(string space, string name)
    {
        var state = Json($"assets/{space}/blockstates/{name}.json");
        JsonNode? chosen = (state?["variants"] as JsonObject)?.FirstOrDefault().Value;
        if (chosen is JsonArray variants) chosen = variants.FirstOrDefault();
        string? model = chosen?["model"]?.ToString();
        model ??= $"{space}:block/{name}";
        var textures = new Dictionary<string, string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < 12 && model != null && seen.Add(model); depth++)
        {
            var parts = model.Split(':'); string ns = parts.Length == 2 ? parts[0] : "minecraft", path = parts[^1];
            if (!Resource(ns) || !Resource(path)) break;
            var json = Json($"assets/{ns}/models/{path}.json"); if (json == null) break;
            if (json["textures"] is JsonObject own) foreach (var texture in own) if (!textures.ContainsKey(texture.Key) && texture.Value != null) textures[texture.Key] = texture.Value.ToString();
            // Explicit upwards faces cover custom cube models without inventing a 3D block shape.
            if (json["elements"] is JsonArray elements)
                foreach (var element in elements)
                    if (element?["faces"]?["up"]?["texture"]?.ToString() is { } up) textures.TryAdd("up", up);
            model = json["parent"]?.ToString();
        }
        foreach (string key in new[] { "up", "top", "all", "end", "texture", "particle", "side" })
            if (textures.TryGetValue(key, out string? value))
            {
                var references = new HashSet<string>(StringComparer.Ordinal);
                while (value != null && value.StartsWith('#') && references.Add(value) && references.Count < 16)
                    if (!textures.TryGetValue(value[1..], out value)) break;
                if (value != null && !value.StartsWith('#')) return value;
            }
        return null;
    }
    byte[]? ReadTile(string path)
    {
        var entry = archive.GetEntry(path); if (entry == null || entry.Length is <= 0 or > 4 * 1024 * 1024) return null;
        using var input = entry.Open(); var bytes = new byte[(int)entry.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        if (width is < 1 or > 1024 || height < width || height > 16384 || (long)width * height > 4_194_304) return null;
        using var data = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height) return null;
        // Animated block atlases use their first square frame; frames are not flattened together.
        var bgra = new FormatConvertedBitmap(new CroppedBitmap(frame, new System.Windows.Int32Rect(0, 0, width, width)), PixelFormats.Bgra32, null, 0);
        var source = new byte[width * width * 4]; bgra.CopyPixels(source, width * 4, 0);
        var tile = new byte[4 * 4 * 4];
        for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++)
        {
            long b = 0, g = 0, r = 0, alpha = 0, count = 0;
            int x1 = x * width / 4, x2 = Math.Max(x1 + 1, (x + 1) * width / 4), z1 = z * width / 4, z2 = Math.Max(z1 + 1, (z + 1) * width / 4);
            for (int zz = z1; zz < z2 && zz < width; zz++) for (int xx = x1; xx < x2 && xx < width; xx++)
            { int i = (zz * width + xx) * 4, a = source[i + 3]; b += source[i] * a; g += source[i + 1] * a; r += source[i + 2] * a; alpha += a; count++; }
            int target = (z * 4 + x) * 4;
            if (alpha > 0) { tile[target] = (byte)(b / alpha); tile[target + 1] = (byte)(g / alpha); tile[target + 2] = (byte)(r / alpha); }
            tile[target + 3] = count > 0 ? (byte)(alpha / count) : (byte)0;
        }
        return tile;
    }
    static string Legacy(string name)
    {
        if (name is "grass_block_top" or "dirt_path_top") return "grass_top";
        foreach (string wood in new[] { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak" })
        {
            if (name == wood + "_planks") return "planks_" + wood;
            if (name == wood + "_leaves") return "leaves_" + wood;
            if (name == wood + "_log") return "log_" + wood + "_top";
        }
        return name switch { "cobblestone_mossy" => "mossy_cobblestone", "bricks" => "brick", "stone_bricks" => "stonebrick", "nether_bricks" => "nether_brick", "white_wool" => "wool_colored_white", _ => name };
    }
    public static int TintColor(string block, string? biome, int kind)
    {
        string name = biome ?? "";
        if (kind == 2)
        {
            if (block.Contains("spruce")) return 0x619961;
            if (block.Contains("birch")) return 0x80a755;
            if (name.Contains("swamp")) return 0x6a7039;
            if (name.Contains("badlands")) return 0x9e814d;
            if (name.Contains("jungle")) return 0x30bb0b;
            return name.Contains("desert") || name.Contains("savanna") ? 0xaea42a : 0x77ab2f;
        }
        if (name.Contains("swamp")) return 0x6a7039;
        if (name.Contains("badlands")) return 0x90814d;
        if (name.Contains("dark_forest")) return 0x507a32;
        if (name.Contains("jungle")) return 0x59c93c;
        if (name.Contains("desert") || name.Contains("savanna")) return 0xbfb755;
        if (name.Contains("snow") || name.Contains("frozen")) return 0x80b497;
        if (name.Contains("forest")) return 0x79c05a;
        if (name.Contains("taiga")) return 0x86b783;
        return 0x91bd59;
    }
    public void Dispose() => archive.Dispose();
}
