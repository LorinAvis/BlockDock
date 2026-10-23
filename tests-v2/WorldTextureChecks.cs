using BlockDock;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class WorldTextureChecks
{
    static void Check(bool good, string label) { if (!good) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    static Dictionary<string, string> Hashes(string folder) => WorldFiles.Tree(folder, default).ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    static void Text(ZipArchive zip, string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(text); }
    static void Texture(ZipArchive zip, string name, bool patterned = false)
    {
        var bytes = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
        {
            int i = (y * 16 + x) * 4; bool alternate = (x / 4 + y / 4) % 2 == 1;
            bytes[i] = patterned ? (byte)(alternate ? 64 : 128) : (byte)255;
            bytes[i + 1] = patterned ? (byte)(alternate ? 224 : 16) : (byte)255;
            bytes[i + 2] = patterned ? (byte)(alternate ? 24 : 240) : (byte)255; bytes[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, bytes, 64); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var encoded = new MemoryStream(); encoder.Save(encoded); encoded.Position = 0;
        using var output = zip.CreateEntry("assets/minecraft/textures/block/" + name + ".png").Open(); encoded.CopyTo(output);
    }
    static int Pixel(WorldMapImage image, int x, int z, int dx = 0, int dz = 0)
    {
        int scale = image.DetailWidth / image.Width, index = (((z - image.MinZ) * scale + dz) * image.DetailWidth + (x - image.MinX) * scale + dx) * 4;
        var data = image.DetailPixels!; return data[index + 2] << 16 | data[index + 1] << 8 | data[index];
    }
    public static void Run()
    {
        string root = Path.GetFullPath("work/world-texture-checks/" + Guid.NewGuid().ToString("N")), id = "texture-fixture-instance", parent = "texture-fixture-parent";
        string folder = Path.Combine(root, "versions", id), vanilla = Path.Combine(root, "versions", parent); Directory.CreateDirectory(folder); Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(folder, id + ".json"), "{\"inheritsFrom\":\"" + parent + "\"}");
        using (var loader = ZipFile.Open(Path.Combine(folder, id + ".jar"), ZipArchiveMode.Create)) Text(loader, "loader.txt", "Synthetic fixture only");
        using (var zip = ZipFile.Open(Path.Combine(vanilla, parent + ".jar"), ZipArchiveMode.Create))
        {
            foreach (string texture in new[] { "stone", "grass_block_top", "water_still", "sand", "oak_leaves" }) Texture(zip, texture);
            Texture(zip, "fixture_roof", true);
            Text(zip, "assets/minecraft/models/block/oak_planks.json", "{\"parent\":\"minecraft:block/fixture_cube\",\"textures\":{\"roof\":\"minecraft:block/fixture_roof\"}}");
            Text(zip, "assets/minecraft/models/block/fixture_cube.json", "{\"textures\":{\"top\":\"#roof\"}}");
        }
        var source = new WorldSource("Texture fixture", id, false, Path.Combine(folder, "saves"));
        var world = WorldFixtures.World(source, "harbor", "Texture fixture", 123); WorldFixtures.Demo(world); var before = Hashes(world.Folder);
        using (var textures = WorldMapTextures.Open(world, default))
        {
            Check(textures?.Version == parent, "texture reader follows selected loader's parent game and ignores loader-only jar");
            var roof = textures!.Get("minecraft:oak_planks");
            Check(roof is { Tint: 0 } && roof.Pixels[0] == 128 && roof.Pixels[1] == 16 && roof.Pixels[2] == 240 && roof.Pixels[5] == 224, "top-face model inheritance resolves actual synthetic PNG pixels");
            Check(textures.Get("minecraft:grass_block")?.Tint == 1 && textures.Get("minecraft:oak_leaves")?.Tint == 2 && textures.Get("minecraft:water")?.Tint == 3, "grass, foliage and water textures retain distinct tint behavior");
            Check(textures.Get("minecraft:../../outside") == null && textures.Get("minecraft:/absolute") == null, "texture resource paths cannot escape the game asset namespace");
        }
        var map = WorldMapRenderer.Render(world, 0, 0, 256, 0, null, default);
        Check(map.TexturedBlocks > 0 && map.DetailWidth > map.Width && map.DetailWidth <= 2048 && map.DetailPixels?.Length == map.DetailWidth * map.DetailWidth * 4, "local map has a bounded nonempty texture detail layer");
        int pink = Pixel(map, 25, 25), green = Pixel(map, 25, 25, 1);
        Check((pink >> 16 & 255) > (pink >> 8 & 255) * 4 && (green >> 8 & 255) > (green >> 16 & 255) * 4, "detail map shows the installed roof texture pattern instead of a flat replacement color");
        var after = Hashes(world.Folder); Check(before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "texture rendering leaves all original world files unchanged");
        var missing = world with { Version = "missing-" + Guid.NewGuid().ToString("N"), Source = new WorldSource("Missing", "missing-texture-version", false, Path.Combine(root, "saves")) };
        using (var unrelated = WorldMapTextures.Open(missing, default)) Check(unrelated == null, "missing game version does not substitute unrelated installed jar textures");
        Check(WorldMapTextures.TintColor("minecraft:grass_block", "minecraft:desert", 1) != WorldMapTextures.TintColor("minecraft:grass_block", "minecraft:jungle", 1), "biome grass tint distinguishes dry and humid terrain");
        SurfaceChecks(source);
        Console.WriteLine("WORLD TEXTURE CHECKS PASSED; generated test pixels only");
    }
    static void SurfaceChecks(WorldSource source)
    {
        string[] names = { "air", "grass_block", "tall_grass", "dandelion", "water", "kelp", "oak_leaves" };
        var blocks = new int[4096];
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            blocks[z * 16 + x] = x is 3 or 4 ? 4 : 1;
            blocks[256 + z * 16 + x] = x switch { 1 => 2, 2 => 3, 3 => 5, 4 => 4, 6 => 6, _ => 0 };
        }
        WorldMapImage Render(string name, int[] content)
        {
            var world = WorldFixtures.World(source, name, "Surface fixture", 0);
            var section = new Dictionary<string, object> { ["Y"] = (sbyte)4, ["block_states"] = new Dictionary<string, object> {
                ["palette"] = new WorldFixtures.Tags(10, names.Select(n => (object)new Dictionary<string, object> { ["Name"] = "minecraft:" + n }).ToArray()), ["data"] = WorldFixtures.Packed(content, 4, false) },
                ["biomes"] = new Dictionary<string, object> { ["palette"] = new WorldFixtures.Tags(8, new object[] { "minecraft:jungle" }) } };
            WorldFixtures.Region(Path.Combine(world.Folder, "region"), 0, 0, new[] { (0, 0, new Dictionary<string, object> { ["sections"] = new WorldFixtures.Tags(10, new object[] { section }) }) });
            return WorldMapRenderer.Render(world, 0, 0, 256, 0, null, default);
        }
        // Compare identical coordinates and neighboring terrain so slope lighting does not
        // masquerade as a difference in plant filtering or water texture selection.
        var baselineBlocks = blocks.Select(block => block switch { 2 or 3 => 0, 5 => 4, _ => block }).ToArray();
        var map = Render("surface", blocks); var baseline = Render("surface-baseline", baselineBlocks);
        Check(Pixel(map, 1, 8) == Pixel(baseline, 1, 8) && Pixel(map, 2, 8) == Pixel(baseline, 2, 8), "grass and flower overlays reveal the saved ground texture below");
        Check(Pixel(map, 3, 8) == Pixel(baseline, 3, 8), "kelp columns render the same water surface and depth as open water");
        var withoutTrees = Render("surface-no-trees", baselineBlocks.Select(block => block == 6 ? 0 : block).ToArray());
        Check(Pixel(baseline, 6, 8) != Pixel(withoutTrees, 6, 8), "tree foliage remains visible while small ground plants are filtered");
        int grass = Pixel(map, 0, 8); Check((grass >> 8 & 255) > (grass >> 16 & 255), "modern chunk biome palette produces green jungle grass tint");
    }
}
