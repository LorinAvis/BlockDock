using System.IO;
using System.Runtime.InteropServices;

namespace BlockDock;

public sealed record SeedAlgorithm(int Index, string Family, string Baseline)
{
    public override string ToString() => "Java " + Family + " · 基准 " + Baseline;
}
public sealed record SeedFeature(string Name, int X, int Z);
public sealed record SeedMapResult(WorldMapImage Image, int[] Biomes, IReadOnlyList<SeedFeature> Features, SeedAlgorithm Algorithm);

public static class OfflineSeedMap
{
    public static readonly SeedAlgorithm[] Algorithms = new[] {
        new SeedAlgorithm(0,"1.7","1.7.10"), new(1,"1.8","1.8.9"), new(2,"1.9","1.9.4"), new(3,"1.10","1.10.2"),
        new(4,"1.11","1.11.2"), new(5,"1.12","1.12.2"), new(6,"1.13","1.13.2"), new(7,"1.14","1.14.4"),
        new(8,"1.15","1.15.2"), new(9,"1.16.0–1.16.1","1.16.1"), new(10,"1.16.2–1.16.5","1.16.5"),
        new(11,"1.17","1.17.1"), new(12,"1.18","1.18.2"), new(13,"1.19.0–1.19.2","1.19.2"),
        new(14,"1.19.3–1.19.4","1.19.4"), new(15,"1.20","1.20.6"), new(16,"1.21.0–1.21.1","1.21.1"), new(17,"1.21.2–1.21.3","1.21.3") };
    public static SeedAlgorithm? Match(string version)
    {
        if (!Version.TryParse(version, out var v) || v.Major != 1 || v.Revision >= 0) return null;
        int patch = Math.Max(v.Build, 0);
        if (v.Minor == 21) return patch <= 1 ? Algorithms[16] : patch <= 3 ? Algorithms[17] : null;
        if (v.Minor == 16) return patch <= 1 ? Algorithms[9] : patch <= 5 ? Algorithms[10] : null;
        if (v.Minor == 19) return patch <= 2 ? Algorithms[13] : patch <= 4 ? Algorithms[14] : null;
        return Algorithms.FirstOrDefault(a => a.Family == "1." + v.Minor && patch <= Math.Max(0, Version.Parse(a.Baseline).Build));
    }
    static readonly string[] Features = { "村庄", "沙漠神殿", "丛林神庙", "沼泽小屋", "雪屋", "海底神殿", "林地府邸", "掠夺者前哨站", "废弃传送门", "远古城市", "埋藏的宝藏", "古迹废墟", "试炼密室", "下界要塞", "堡垒遗迹", "末地城", "下界废弃传送门" };
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Abi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RenderNative(int version, long seed, int dimension, int minX, int minZ, int scale, int width, int large, [Out] int[] colors, [Out] int[] biomes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FeaturesNative(int version, long seed, int dimension, int minX, int minZ, int span, int large, [Out] int[] output, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BiomeNative(int version, long seed, int x, int z);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr BiomeNameNative(int version, int biome);
    sealed class Engine
    {
        public readonly RenderNative Render;
        public readonly FeaturesNative Features;
        public readonly BiomeNative Biome;
        public readonly BiomeNameNative Name;
        public Engine()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "engines", "SeedMap", "BlockDock.SeedMap.dll");
            if (!File.Exists(path)) throw new IOException("缺少离线预测组件，请使用完整 Windows 下载包，源码构建请先运行 build-seed-map.ps1。");
            var module = NativeLibrary.Load(path);
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
            if (Function<Abi>("bd_seed_abi")() != 1) throw new IOException("离线预测组件版本不匹配。");
            Render = Function<RenderNative>("bd_seed_render"); Features = Function<FeaturesNative>("bd_seed_features");
            Biome = Function<BiomeNative>("bd_seed_biome"); Name = Function<BiomeNameNative>("bd_biome_name");
        }
    }
    static readonly Lazy<Engine> Native = new(() => new());
    public static int BiomeAt(SeedAlgorithm algorithm, long seed, int x, int z) => Native.Value.Biome(algorithm.Index, seed, x, z);
    public static string BiomeName(SeedAlgorithm algorithm, int biome) => Marshal.PtrToStringUTF8(Native.Value.Name(algorithm.Index, biome)) ?? "unknown";
    public static string BiomeDisplayName(SeedAlgorithm algorithm, int biome)
    {
        string name = BiomeName(algorithm, biome);
        return BiomeNames.GetValueOrDefault(name, name.Replace('_', ' '));
    }
    static readonly IReadOnlyDictionary<string, string> BiomeNames = new Dictionary<string, string>
    {
        ["ocean"]="海洋", ["deep_ocean"]="深海", ["warm_ocean"]="暖水海洋", ["deep_warm_ocean"]="暖水深海",
        ["lukewarm_ocean"]="温水海洋", ["deep_lukewarm_ocean"]="温水深海", ["cold_ocean"]="冷水海洋", ["deep_cold_ocean"]="冷水深海",
        ["frozen_ocean"]="冻洋", ["deep_frozen_ocean"]="冰冻深海", ["river"]="河流", ["frozen_river"]="冻河",
        ["plains"]="平原", ["sunflower_plains"]="向日葵平原", ["desert"]="沙漠", ["desert_hills"]="沙漠丘陵", ["desert_lakes"]="沙漠湖泊",
        ["forest"]="森林", ["wooded_hills"]="繁茂丘陵", ["flower_forest"]="繁花森林", ["birch_forest"]="桦木森林", ["birch_forest_hills"]="桦木丘陵",
        ["old_growth_birch_forest"]="原始桦木森林", ["tall_birch_forest"]="高大桦木森林", ["tall_birch_hills"]="高大桦木丘陵",
        ["dark_forest"]="黑森林", ["dark_forest_hills"]="黑森林丘陵", ["taiga"]="针叶林", ["taiga_hills"]="针叶丘陵", ["taiga_mountains"]="针叶山地",
        ["giant_tree_taiga"]="巨型针叶林", ["giant_tree_taiga_hills"]="巨型针叶丘陵", ["giant_spruce_taiga"]="巨型云杉林", ["giant_spruce_taiga_hills"]="巨型云杉丘陵",
        ["old_growth_pine_taiga"]="原始松木针叶林", ["old_growth_spruce_taiga"]="原始云杉针叶林",
        ["swamp"]="沼泽", ["swamp_hills"]="沼泽丘陵", ["mangrove_swamp"]="红树林沼泽",
        ["jungle"]="丛林", ["jungle_hills"]="丛林丘陵", ["jungle_edge"]="丛林边缘", ["sparse_jungle"]="稀疏丛林", ["modified_jungle"]="丛林变种", ["modified_jungle_edge"]="丛林边缘变种",
        ["bamboo_jungle"]="竹林", ["bamboo_jungle_hills"]="竹林丘陵", ["savanna"]="热带草原", ["savanna_plateau"]="热带高原", ["shattered_savanna"]="破碎热带草原", ["shattered_savanna_plateau"]="破碎热带高原", ["windswept_savanna"]="风袭热带草原",
        ["badlands"]="恶地", ["badlands_plateau"]="恶地高原", ["wooded_badlands_plateau"]="繁茂恶地高原", ["wooded_badlands"]="繁茂恶地", ["eroded_badlands"]="风蚀恶地", ["modified_badlands_plateau"]="恶地高原变种", ["modified_wooded_badlands_plateau"]="繁茂恶地高原变种",
        ["mountains"]="山地", ["wooded_mountains"]="繁茂山地", ["mountain_edge"]="山地边缘", ["gravelly_mountains"]="沙砾山地", ["modified_gravelly_mountains"]="沙砾山地变种",
        ["windswept_hills"]="风袭丘陵", ["windswept_forest"]="风袭森林", ["windswept_gravelly_hills"]="风袭沙砾丘陵", ["stony_peaks"]="裸岩山峰", ["jagged_peaks"]="尖峭山峰", ["frozen_peaks"]="冰封山峰",
        ["snowy_plains"]="雪原", ["snowy_tundra"]="积雪冻原", ["snowy_mountains"]="雪山", ["snowy_taiga"]="积雪针叶林", ["snowy_taiga_hills"]="积雪针叶丘陵", ["snowy_taiga_mountains"]="积雪针叶山地", ["ice_spikes"]="冰刺之地", ["snowy_slopes"]="积雪山坡", ["grove"]="雪林",
        ["beach"]="沙滩", ["stone_shore"]="石岸", ["stony_shore"]="石岸", ["snowy_beach"]="积雪沙滩", ["mushroom_fields"]="蘑菇岛", ["mushroom_field_shore"]="蘑菇岛岸", ["meadow"]="草甸", ["cherry_grove"]="樱花树林",
        ["dripstone_caves"]="溶洞", ["lush_caves"]="繁茂洞穴", ["deep_dark"]="深暗之域", ["nether_wastes"]="下界荒地", ["soul_sand_valley"]="灵魂沙峡谷", ["crimson_forest"]="绯红森林", ["warped_forest"]="诡异森林", ["basalt_deltas"]="玄武岩三角洲",
        ["the_end"]="末地", ["small_end_islands"]="末地小型岛屿", ["end_midlands"]="末地内陆", ["end_highlands"]="末地高地", ["end_barrens"]="末地荒岛", ["the_void"]="虚空"
    };
    public static int BiomeColor(SeedAlgorithm algorithm, int biome) => CartographicColor(BiomeName(algorithm, biome));
    static int CartographicColor(string name)
    {
        // Color represents only a biome category; no invented elevation or terrain is applied.
        if (name.Contains("ocean")) return name.Contains("frozen") ? name.Contains("deep") ? 0x426b89 : 0x81a8b9
            : name.Contains("warm") ? name.Contains("deep") ? 0x286a89 : 0x4d9fa9
            : name.Contains("cold") ? name.Contains("deep") ? 0x27475f : 0x416b87
            : name.Contains("deep") ? 0x284f6b : 0x497f9a;
        if (name.Contains("river")) return name.Contains("frozen") ? 0xa0c3cd : 0x6ba3b5;
        if (name.Contains("mushroom")) return 0xb39aac;
        if (name.Contains("cherry")) return 0xd8aeb8;
        if (name.Contains("snow") || name.Contains("frozen_peaks") || name == "ice_spikes" || name == "grove") return name.Contains("taiga") || name == "grove" ? 0xa1b8ad : 0xd8e2df;
        if (name.Contains("badlands")) return name.Contains("wooded") ? 0xa78b63 : name.Contains("eroded") ? 0xb37e5c : 0xc4956b;
        if (name.Contains("desert") || name == "beach") return name.Contains("hills") ? 0xcbbb8b : 0xd8ca9d;
        if (name.Contains("shore")) return 0x9baba3;
        if (name.Contains("swamp")) return name.Contains("mangrove") ? 0x617e65 : 0x7b9270;
        if (name.Contains("jungle")) return name.Contains("bamboo") ? 0x759755 : name.Contains("edge") || name.Contains("sparse") ? 0x8aa569 : 0x4f7e54;
        if (name.Contains("savanna")) return name.Contains("plateau") || name.Contains("windswept") ? 0xb3b479 : 0xbec18a;
        if (name.Contains("birch")) return name.Contains("old_growth") || name.Contains("tall") ? 0x87a66c : 0x9bb77b;
        if (name.Contains("taiga")) return name.Contains("old_growth") || name.Contains("giant") ? 0x557c68 : 0x6d927e;
        if (name.Contains("forest") || name.Contains("wooded")) return name.Contains("dark") ? 0x47674e : name.Contains("flower") ? 0x91af78 : 0x73965e;
        if (name.Contains("mountain") || name.Contains("hills") || name.Contains("peaks")) return name.Contains("gravelly") || name.Contains("stony") ? 0x9da595 : 0x94a48c;
        if (name == "meadow") return 0xb0c68a;
        if (name.Contains("plains")) return name.Contains("sunflower") ? 0xc0cb8d : 0xa5bd82;
        return name switch { "nether_wastes" => 0x8a514a, "soul_sand_valley" => 0x9d8871, "crimson_forest" => 0x9d4e5a, "warped_forest" => 0x4a8c88, "basalt_deltas" => 0x696f70,
            "the_end" => 0xc4c39b, "small_end_islands" => 0x979d87, "end_midlands" => 0xb2b68d, "end_highlands" => 0xd0cea2, "end_barrens" => 0x8c9489,
            "dripstone_caves" => 0xa69880, "lush_caves" => 0x82a26e, "deep_dark" => 0x44626c, "the_void" => 0x28343e, _ => 0x9ba99b };
    }
    public static SeedMapResult Render(bool bedrock, SeedAlgorithm algorithm, long seed, int centerX, int centerZ, int span,
        int dimension, bool large, IProgress<string>? progress, CancellationToken ct)
    {
        if (bedrock) throw new IOException("基岩版离线种子预测尚未适配。可使用本地地图查看已探索的世界。");
        if (algorithm.Index < 0 || algorithm.Index >= Algorithms.Length || Algorithms[algorithm.Index] != algorithm
            || dimension is < 0 or > 2 || span is not (1024 or 4096 or 16384)
            || Math.Abs((long)centerX) > 30_000_000 || Math.Abs((long)centerZ) > 30_000_000) throw new IOException("预测地图参数无效。");
        ct.ThrowIfCancellationRequested(); progress?.Report("离线计算生物群系…");
        int width = 1024, step = span / width, minX = WorldMapRenderer.Floor(centerX - span / 2, step) * step, minZ = WorldMapRenderer.Floor(centerZ - span / 2, step) * step;
        int dim = dimension == 1 ? -1 : dimension == 2 ? 1 : 0;
        var colors = new int[width * width]; var biomes = new int[colors.Length];
        if (Native.Value.Render(algorithm.Index, seed, dim, minX, minZ, step, width, large ? 1 : 0, colors, biomes) != 0) throw new IOException("此版本或维度尚不能生成预测地图。");
        ct.ThrowIfCancellationRequested(); progress?.Report("离线查找结构位置…");
        var raw = new int[4096 * 3]; int count = Native.Value.Features(algorithm.Index, seed, dim, minX, minZ, span, large ? 1 : 0, raw, 4096);
        if (count < 0 || count > 4096) throw new IOException("结构预测未完成。");
        ct.ThrowIfCancellationRequested(); var features = new List<SeedFeature>();
        for (int i = 0; i < count; i++) if (raw[i * 3] >= 0 && raw[i * 3] < Features.Length) features.Add(new(Features[raw[i * 3]], raw[i * 3 + 1], raw[i * 3 + 2]));
        var palette = biomes.Distinct().ToDictionary(b => b, b => BiomeColor(algorithm, b));
        var pixels = new byte[colors.Length * 4];
        for (int i = 0; i < colors.Length; i++) { if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); int color = palette[biomes[i]]; pixels[i * 4] = (byte)color; pixels[i * 4 + 1] = (byte)(color >> 8); pixels[i * 4 + 2] = (byte)(color >> 16); pixels[i * 4 + 3] = 255; }
        return new(new(width, step, minX, minZ, pixels, 0, 0), biomes, features, algorithm);
    }
}
