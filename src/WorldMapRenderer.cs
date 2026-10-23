using System.Buffers.Binary;
using System.IO;

namespace BlockDock;

public sealed record WorldMapImage(int Width, int BlocksPerPixel, int MinX, int MinZ, byte[] Pixels, int Chunks, int Unreadable,
    int DetailWidth = 0, byte[]? DetailPixels = null, int TexturedBlocks = 0, string TextureVersion = "");

public static class WorldMapRenderer
{
    public static WorldMapImage Render(SavedWorld world, int centerX, int centerZ, int span, int dimension,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (Math.Abs((long)centerX) > 30_000_000 || Math.Abs((long)centerZ) > 30_000_000 || span is not (256 or 512 or 1024) || dimension is < 0 or > 2) throw new IOException("地图坐标或范围无效。");
        using var lease = WorldFiles.Lease(world);
        using var textures = WorldMapTextures.Open(world, ct);
        int step = 1, width = span; // Keep one saved block per pixel throughout the local-map range.
        int minX = Floor(centerX - span / 2, 16) * 16, minZ = Floor(centerZ - span / 2, 16) * 16;
        var pixels = new byte[width * width * 4]; var heights = Enumerable.Repeat(int.MinValue, width * width).ToArray();
        var water = new bool[heights.Length]; var blockColors = new Dictionary<string, int>(StringComparer.Ordinal);
        var surfaces = textures != null ? new ushort[heights.Length] : null;
        var tints = textures != null ? new int[heights.Length] : null;
        var substrates = textures != null ? new int[heights.Length] : null;
        var lighting = textures != null ? Enumerable.Repeat((byte)200, heights.Length).ToArray() : null;
        var surfaceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var tileTypes = new List<WorldMapTextures.Tile>(); int texturedBlocks = 0;
        for (int i = 0; i < heights.Length; i++) { byte shade = (byte)(25 + ((i % width / 16 + i / width / 16) % 2) * 7); pixels[i * 4] = (byte)(shade + 7); pixels[i * 4 + 1] = (byte)(shade + 3); pixels[i * 4 + 2] = shade; pixels[i * 4 + 3] = 255; }
        int chunks = 0, errors = 0;
        void Draw(int cx, int cz, List<Section> sections, byte[]? legacy = null, byte[]? legacyAdd = null)
        {
            ct.ThrowIfCancellationRequested(); chunks++;
            sections.Sort((a, b) => b.Y.CompareTo(a.Y));
            for (int z = 0; z < 16; z += step) for (int x = 0; x < 16; x += step)
            {
                int px = (cx * 16 + x - minX) / step, pz = (cz * 16 + z - minZ) / step;
                if (px < 0 || pz < 0 || px >= width || pz >= width) continue;
                string block = "minecraft:air"; int height = 0; bool foundSurface = false;
                if (legacy != null)
                {
                    for (int y = 127; y >= 0; y--)
                    {
                        if (dimension == 1 && y > 120) continue;
                        int n = x * 2048 + z * 128 + y; block = SurfaceBlock(Legacy(legacy[n] | Add(legacyAdd, n)));
                        if (!Air(block) && !(dimension == 1 && block.Contains("bedrock"))) { height = y; foundSurface = true; break; }
                    }
                }
                else foreach (var section in sections)
                {
                    bool found = false;
                    for (int y = 15; y >= 0; y--)
                    {
                        int globalY = section.Y * 16 + y;
                        if (dimension == 1 && globalY > 120) continue;
                        block = SurfaceBlock(section.Block(x, y, z));
                        if (!Air(block) && !(dimension == 1 && block.Contains("bedrock"))) { height = globalY; found = foundSurface = true; break; }
                    }
                    if (found) break;
                }
                if (!foundSurface) continue;
                int index = pz * width + px; heights[index] = height;
                if (!blockColors.TryGetValue(block, out int color)) blockColors[block] = color = Color(block);
                string At(int y) => SurfaceBlock(legacy != null && y is >= 0 and < 128 ? Legacy(legacy[x * 2048 + z * 128 + y] | Add(legacyAdd, x * 2048 + z * 128 + y))
                    : sections.FirstOrDefault(s => s.Y == Floor(y, 16))?.Block(x, y & 15, z) ?? "minecraft:air");
                if (textures != null)
                {
                    if (!surfaceTypes.TryGetValue(block, out int type))
                    {
                        var tile = textures.Get(block); type = tile != null && tileTypes.Count < 4096 ? tileTypes.Count + 1 : 0;
                        if (type != 0) tileTypes.Add(tile!); surfaceTypes[block] = type;
                    }
                    if (type != 0)
                    {
                        texturedBlocks++; surfaces![index] = (ushort)type; var tile = tileTypes[type - 1];
                        string? biome = sections.FirstOrDefault(s => s.Y == Floor(height, 16))?.Biome(x, height & 15, z);
                        tints![index] = WorldMapTextures.TintColor(block, biome, tile.Tint);
                        substrates![index] = color;
                        if (tile.Transparent && tile.Tint != 3)
                            for (int y = height - 1; y >= height - 48; y--)
                            {
                                string under = At(y);
                                if (Air(under) || under.Contains("leaves") || under.Contains("glass") || under.Contains("grass") && !under.Contains("block")) continue;
                                substrates[index] = Color(under); break;
                            }
                    }
                }
                if (block.Contains("water") || block.Contains("kelp") || block.Contains("seagrass"))
                {
                    water[index] = true; int depth = 0;
                    for (int y = height; y >= height - 31; y--)
                    {
                        string below = At(y);
                        if (!below.Contains("water") && !below.Contains("kelp") && !below.Contains("seagrass")) break;
                        depth++;
                    }
                    color = Blend(0x74a9ac, color, Math.Clamp(depth / 18.0, 0, 1));
                }
                pixels[index * 4] = (byte)color; pixels[index * 4 + 1] = (byte)(color >> 8); pixels[index * 4 + 2] = (byte)(color >> 16);
            }
        }
        bool Inside(int x, int z) => x >= Floor(minX, 16) && x < Floor(minX + span, 16) && z >= Floor(minZ, 16) && z < Floor(minZ + span, 16);
        if (world.Source.Bedrock)
        {
            bool Wanted(byte[] key) => Coordinates(key) is { } c && c.Dimension == dimension && Inside(c.X, c.Z) && c.Tag is 47 or 48;
            var entries = new BedrockWorldReader(Wanted, ct).Read(Path.Combine(world.Folder, "db"), progress);
            foreach (var group in entries.Where(e => e.Value != null).GroupBy(e => { var c = Coordinates(e.Key)!.Value; return (c.X, c.Z); }))
            {
                var sections = new List<Section>(); byte[]? legacy = null;
                foreach (var entry in group)
                    try
                    {
                        var coord = Coordinates(entry.Key)!.Value;
                        if (coord.Tag == 48 && entry.Value!.Length >= 32768) legacy = entry.Value;
                        else if (coord.Tag == 47) sections.Add(BedrockSection(entry.Value!, unchecked((sbyte)entry.Key[^1])));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or OverflowException) { errors++; }
                try { Draw(group.Key.X, group.Key.Z, sections, legacy); }
                catch (InvalidDataException) { errors++; }
            }
        }
        else
        {
            string dimensionName = dimension switch { 1 => "the_nether", 2 => "the_end", _ => "overworld" };
            string traditional = dimension switch { 1 => "DIM-1/region", 2 => "DIM1/region", _ => "region" };
            string modern = Paths.Safe(world.Folder, "dimensions/minecraft/" + dimensionName + "/region");
            string folder = Directory.Exists(modern) ? modern : Paths.Safe(world.Folder, traditional);
            WorldFiles.NoLinks(folder);
            for (int rz = Floor(minZ, 512); rz <= Floor(minZ + span - 1, 512); rz++)
                for (int rx = Floor(minX, 512); rx <= Floor(minX + span - 1, 512); rx++)
                {
                    ct.ThrowIfCancellationRequested(); string file = Path.Combine(folder, $"r.{rx}.{rz}.mca");
                    if (!File.Exists(file)) file = Path.Combine(folder, $"r.{rx}.{rz}.mcr"); if (!File.Exists(file)) continue;
                    WorldFiles.NoLinks(file); progress?.Report($"读取地图 · 区域 {rx}, {rz}");
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var header = new byte[4096]; if (stream.Length < 8192) { errors++; continue; } stream.ReadExactly(header);
                    for (int i = 0; i < 1024; i++)
                    {
                        int cx = rx * 32 + i % 32, cz = rz * 32 + i / 32;
                        if (!Inside(cx, cz)) continue;
                        int sector = (header[i * 4] << 16) | (header[i * 4 + 1] << 8) | header[i * 4 + 2], sectors = header[i * 4 + 3];
                        if (sector == 0) continue; ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (sector < 2 || sectors == 0 || (long)sector * 4096 + 5 > stream.Length) throw new InvalidDataException();
                            stream.Position = (long)sector * 4096; var lengthBytes = new byte[4]; stream.ReadExactly(lengthBytes);
                            int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes); int compression = stream.ReadByte();
                            if (length < 1 || length > sectors * 4096 - 4 || stream.Position + length - 1 > stream.Length) throw new InvalidDataException();
                            byte[] payload;
                            if ((compression & 128) != 0)
                            {
                                string external = Path.Combine(folder, $"c.{cx}.{cz}.mcc"); WorldFiles.NoLinks(external);
                                using var input = File.OpenRead(external); payload = WorldNbt.ReadLimited(input);
                            }
                            else { payload = new byte[length - 1]; stream.ReadExactly(payload); }
                            var root = new WorldNbt(WorldNbt.Inflate(payload, compression & 127)).Root(); var level = WorldNbt.GetCompound(root, "Level") ?? root;
                            var list = WorldNbt.List(level, "sections"); if (list.Length == 0) list = WorldNbt.List(level, "Sections");
                            var sections = list.OfType<Dictionary<string, object>>().Select(JavaSection).Where(s => s != null).Cast<Section>().ToList();
                            byte[]? legacy = level.GetValueOrDefault("Blocks") as byte[]; if (legacy?.Length != 32768) legacy = null;
                            Draw(cx, cz, sections, legacy, level.GetValueOrDefault("Add") as byte[]);
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or OverflowException) { errors++; }
                    }
                }
        }
        for (int z = 0; z < width; z++) for (int x = 0; x < width; x++)
        {
            if (x == 0) ct.ThrowIfCancellationRequested();
            int i = z * width + x; if (heights[i] == int.MinValue || water[i]) continue;
            int h = heights[i];
            int Height(int xx, int zz) { int value = heights[Math.Clamp(zz, 0, width - 1) * width + Math.Clamp(xx, 0, width - 1)]; return value == int.MinValue ? h : value; }
            // Northwest lighting comes from the saved height surface, including roofs and tree canopies.
            double dx = Math.Clamp(Height(x + 1, z) - Height(x - 1, z), -12, 12) / (2.0 * step);
            double dz = Math.Clamp(Height(x, z + 1) - Height(x, z - 1), -12, 12) / (2.0 * step);
            double shade = Math.Clamp(1 - (dx + dz) * .045, .58, 1.24);
            if (lighting != null) lighting[i] = (byte)Math.Round(shade * 200);
            for (int c = 0; c < 3; c++) pixels[i * 4 + c] = (byte)Math.Clamp(pixels[i * 4 + c] * shade, 0, 255);
        }
        if (textures == null || texturedBlocks == 0) return new(width, step, minX, minZ, pixels, chunks, errors);
        progress?.Report("绘制本机游戏材质 · 方块与地形…");
        int detailScale = width <= 512 ? 4 : 2, detailWidth = width * detailScale;
        var detail = new byte[detailWidth * detailWidth * 4];
        for (int z = 0; z < width; z++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int cell = z * width + x, type = surfaces![cell], source = cell * 4;
                var tile = type > 0 ? tileTypes[type - 1] : null; int tint = tints![cell], substrate = substrates![cell]; double shade = lighting![cell] / 200.0;
                for (int dz = 0; dz < detailScale; dz++) for (int dx = 0; dx < detailScale; dx++)
                {
                    int target = ((z * detailScale + dz) * detailWidth + x * detailScale + dx) * 4;
                    if (tile == null)
                    { detail[target] = pixels[source]; detail[target + 1] = pixels[source + 1]; detail[target + 2] = pixels[source + 2]; detail[target + 3] = 255; continue; }
                    int sample = (dz * 4 / detailScale * 4 + dx * 4 / detailScale) * 4;
                    double alpha = tile.Tint == 3 ? 1 : tile.Pixels[sample + 3] / 255.0;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double value = tile.Pixels[sample + channel];
                        if (tile.Tint == 3)
                        {
                            // Water textures are greyscale. Preserve the measured depth color while adding their real detail.
                            double mean = tile.Average; value = pixels[source + channel] * Math.Clamp(value / Math.Max(1, mean), .65, 1.35);
                        }
                        else
                        {
                            if (tile.Tint is 1 or 2) value *= ((tint >> (channel * 8)) & 255) / 255.0;
                            value = (value * alpha + ((substrate >> (channel * 8)) & 255) * (1 - alpha)) * shade;
                        }
                        detail[target + channel] = (byte)Math.Clamp(value, 0, 255);
                    }
                    detail[target + 3] = 255;
                }
            }
        }
        return new(width, step, minX, minZ, pixels, chunks, errors, detailWidth, detail, texturedBlocks, textures.Version);
    }
    public static int Floor(int value, int divisor) => (int)Math.Floor((double)value / divisor);
    public static (int X, int Z, int Dimension, int Tag)? Coordinates(byte[] key)
    {
        if (key.Length is not (9 or 10 or 13 or 14)) return null;
        int offset = key.Length >= 13 ? 12 : 8, tag = key[offset];
        if (tag == 47 && key.Length != offset + 2 || tag == 48 && key.Length != offset + 1) return null;
        return (BinaryPrimitives.ReadInt32LittleEndian(key), BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(4)), offset == 12 ? BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(8)) : 0, tag);
    }
    sealed class Section
    {
        public int Y, Bits;
        public bool Bedrock, Dense;
        public string[] Palette = Array.Empty<string>();
        public string[] BiomePalette = Array.Empty<string>();
        public long[] BiomeWords = Array.Empty<long>();
        public long[] Words = Array.Empty<long>();
        public byte[]? LegacyBlocks, LegacyAdd;
        public string? Biome(int x, int y, int z)
        {
            if (BiomePalette.Length == 0) return null;
            if (BiomePalette.Length == 1) return BiomePalette[0];
            int bits = Math.Max(1, (int)Math.Ceiling(Math.Log2(BiomePalette.Length))), per = 64 / bits;
            int index = (y >> 2) * 16 + (z >> 2) * 4 + (x >> 2), word = index / per;
            if (word >= BiomeWords.Length) return null;
            int value = (int)(unchecked((ulong)BiomeWords[word]) >> (index % per * bits) & ((1UL << bits) - 1));
            return value < BiomePalette.Length ? BiomePalette[value] : null;
        }
        public string Block(int x, int y, int z)
        {
            int index = Bedrock ? x * 256 + z * 16 + y : y * 256 + z * 16 + x;
            if (LegacyBlocks != null) return Legacy(LegacyBlocks[index] | Add(LegacyAdd, index));
            if (Palette.Length == 1) return Palette[0];
            if (Bits <= 0 || Palette.Length == 0) return "minecraft:air";
            ulong value;
            if (Dense)
            {
                int offset = index * Bits, word = offset / 64, bit = offset % 64;
                if (word >= Words.Length) throw new InvalidDataException(); value = unchecked((ulong)Words[word]) >> bit;
                if (bit + Bits > 64) { if (word + 1 >= Words.Length) throw new InvalidDataException(); value |= unchecked((ulong)Words[word + 1]) << (64 - bit); }
            }
            else
            {
                int wordBits = Bedrock ? 32 : 64, perWord = wordBits / Bits, word = index / perWord;
                if (word >= Words.Length) throw new InvalidDataException(); value = unchecked((ulong)Words[word]) >> ((index % perWord) * Bits);
            }
            int palette = (int)(value & ((1UL << Bits) - 1));
            if (palette >= Palette.Length) throw new InvalidDataException("区块调色板索引无效。"); return Palette[palette];
        }
    }
    static Section? JavaSection(Dictionary<string, object> section)
    {
        int y = (int)(WorldNbt.Number(section, "Y") ?? 0); if (y is < -128 or > 127) throw new InvalidDataException();
        if (section.GetValueOrDefault("Blocks") is byte[] legacy && legacy.Length >= 4096) return new() { Y = y, LegacyBlocks = legacy, LegacyAdd = section.GetValueOrDefault("Add") as byte[] };
        var states = WorldNbt.GetCompound(section, "block_states");
        string[] palette = WorldNbt.List(states ?? section, states == null ? "Palette" : "palette").OfType<Dictionary<string, object>>().Select(p => WorldNbt.Text(p, "Name")).ToArray();
        if (palette.Length == 0) return null; if (palette.Length > 4096) throw new InvalidDataException();
        var words = (states ?? section).GetValueOrDefault(states == null ? "BlockStates" : "data") as long[] ?? Array.Empty<long>();
        int bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(palette.Length)));
        var biomeData = WorldNbt.GetCompound(section, "biomes");
        var biomes = biomeData == null ? Array.Empty<string>() : WorldNbt.List(biomeData, "palette").OfType<string>().Take(65).ToArray();
        if (biomes.Length > 64) throw new InvalidDataException("区块生物群系调色板过大。");
        return new() { Y = y, Palette = palette, Words = words, Bits = bits, Dense = words.Length == (4096 * bits + 63) / 64,
            BiomePalette = biomes, BiomeWords = biomeData?.GetValueOrDefault("data") as long[] ?? Array.Empty<long>() };
    }
    static Section BedrockSection(byte[] data, int y)
    {
        if (data.Length < 2) throw new InvalidDataException(); int version = data[0];
        if (version < 8 && version != 1)
        { if (data.Length < 4097) throw new InvalidDataException(); return new() { Y = y, Bedrock = true, LegacyBlocks = data.AsSpan(1, 4096).ToArray() }; }
        if (version is not (1 or 8 or 9)) throw new InvalidDataException("暂不支持此基岩版区块版本。");
        int index = version == 1 ? 1 : version == 8 ? 2 : 3;
        if (index >= data.Length || version != 1 && data[1] is < 1 or > 2) throw new InvalidDataException();
        if (version == 9 && unchecked((sbyte)data[2]) != y) throw new InvalidDataException("区块高度记录不匹配。");
        int header = data[index++], bits = header >> 1;
        if ((header & 1) != 0 || bits is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 8 or 16)) throw new InvalidDataException();
        int words = bits == 0 ? 0 : (4096 + 32 / bits - 1) / (32 / bits);
        if (words * 4 > data.Length - index) throw new InvalidDataException();
        var values = new long[words]; for (int i = 0; i < words; i++) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index + i * 4)); index += words * 4;
        int count = 1;
        if (bits > 0 || data.Length - index >= 4 && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index)) == 1)
        { if (data.Length - index < 4) throw new InvalidDataException(); count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index)); index += 4; }
        if (count is < 1 or > 4096) throw new InvalidDataException();
        var palette = new string[count];
        for (int i = 0; i < count; i++) { var nbt = new WorldNbt(data, true, index); var root = nbt.Root(); palette[i] = WorldNbt.Text(root, "name"); index = nbt.Position; }
        return new() { Y = y, Bedrock = true, Bits = bits, Words = values, Palette = palette };
    }
    static bool Air(string name) => name is "" or "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or "minecraft:structure_void" or "minecraft:light";
    // A top-down terrain map shows ground and tree canopies, rather than small upright plant sprites.
    // Aquatic plants still occupy water, so filtering them must not expose the sea floor or alter depth.
    public static string SurfaceBlock(string block)
    {
        if (Air(block)) return block;
        ReadOnlySpan<char> name = block.AsSpan(block.IndexOf(':') + 1);
        if (name is "kelp" or "kelp_plant" or "seagrass" or "tall_seagrass" or "sea_grass" or "tall_sea_grass") return "minecraft:water";
        if (name is "grass" or "short_grass" or "tall_grass" or "tallgrass" or "fern" or "large_fern" or "dead_bush" or "deadbush" or "double_plant" or "doubleplant"
            or "vine" or "vines" or "glow_lichen" or "lichen" or "sculk_vein" or "hanging_roots" or "nether_sprouts" or "crimson_roots" or "warped_roots"
            or "twisting_vines" or "twisting_vines_plant" or "weeping_vines" or "weeping_vines_plant" or "cave_vines" or "cave_vines_plant"
            or "lily_pad" or "waterlily" or "dandelion" or "poppy" or "blue_orchid" or "allium" or "azure_bluet" or "oxeye_daisy" or "cornflower" or "lily_of_the_valley" or "wither_rose"
            or "red_tulip" or "orange_tulip" or "white_tulip" or "pink_tulip" or "sunflower" or "lilac" or "rose_bush" or "peony" or "pink_petals"
            or "torchflower" or "pitcher_plant" or "spore_blossom" or "open_eyeblossom" or "closed_eyeblossom" or "wildflowers" or "cactus_flower"
            or "short_dry_grass" or "tall_dry_grass" or "bush" or "firefly_bush" or "leaf_litter" or "brown_mushroom" or "red_mushroom" or "crimson_fungus" or "warped_fungus"
            or "small_dripleaf" or "big_dripleaf" or "big_dripleaf_stem" or "azalea" or "flowering_azalea") return "minecraft:air";
        return block;
    }
    static int Add(byte[]? values, int index) => values != null && index / 2 < values.Length ? ((values[index / 2] >> ((index & 1) * 4)) & 15) << 8 : 0;
    static int Blend(int a, int b, double amount)
    {
        int Channel(int shift) => (int)Math.Round(((a >> shift) & 255) * (1 - amount) + ((b >> shift) & 255) * amount);
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }
    static string Legacy(int id) => id switch { 0 => "minecraft:air", 1 => "minecraft:stone", 2 => "minecraft:grass_block", 3 => "minecraft:dirt", 4 => "minecraft:cobblestone", 5 => "minecraft:oak_planks", 7 => "minecraft:bedrock", 8 or 9 => "minecraft:water", 10 or 11 => "minecraft:lava", 12 => "minecraft:sand", 17 => "minecraft:oak_log", 18 => "minecraft:oak_leaves", 20 => "minecraft:glass", 24 => "minecraft:sandstone", 31 => "minecraft:tall_grass", 32 => "minecraft:dead_bush", 35 => "minecraft:white_wool", 37 => "minecraft:dandelion", 38 => "minecraft:poppy", 39 => "minecraft:brown_mushroom", 40 => "minecraft:red_mushroom", 45 => "minecraft:bricks", 48 => "minecraft:mossy_cobblestone", 78 or 80 => "minecraft:snow", 79 => "minecraft:ice", 87 => "minecraft:netherrack", 89 => "minecraft:glowstone", 98 => "minecraft:stone_bricks", 106 => "minecraft:vine", 111 => "minecraft:lily_pad", 121 => "minecraft:end_stone", 175 => "minecraft:double_plant", _ => "minecraft:unknown" };
    public static int Color(string block)
    {
        if (block.Contains("water") || block.Contains("kelp") || block.Contains("seagrass")) return 0x396f9c;
        if (block.Contains("lava") || block.Contains("magma")) return 0xe67828;
        if (block.Contains("snow") || block.Contains("quartz") || block.Contains("white_")) return 0xe7ebe8;
        if (block.Contains("ice") || block.Contains("glass")) return 0x9cced9;
        if (block.Contains("red_sand")) return 0xb97948;
        if (block.Contains("sand") || block.Contains("end_stone")) return 0xd5c797;
        if (block.Contains("cherry") || block.Contains("pink_")) return 0xdaa9ba;
        if (block.Contains("spruce") && block.Contains("leaves")) return 0x4a7058;
        if (block.Contains("birch") && block.Contains("leaves")) return 0x83a557;
        if (block.Contains("grass") || block.Contains("moss") || block.Contains("crop") || block.Contains("wheat")) return 0x82a45b;
        if (block.Contains("leaves") || block.Contains("vine") || block.Contains("bamboo")) return 0x46713b;
        if (block.Contains("spruce") || block.Contains("dark_oak")) return 0x72533c;
        if (block.Contains("birch") && (block.Contains("planks") || block.Contains("wood") || block.Contains("log"))) return 0xc5b583;
        if (block.Contains("log") || block.Contains("wood") || block.Contains("planks") || block.Contains("chest")) return 0x9e7950;
        if (block.Contains("path")) return 0xac9763;
        if (block.Contains("dirt") || block.Contains("mud") || block.Contains("farmland")) return 0x836346;
        if (block.Contains("netherrack") || block.Contains("crimson") || block.Contains("red_")) return 0x923e3a;
        if (block.Contains("warped") || block.Contains("cyan_")) return 0x358d87;
        if (block.Contains("black") || block.Contains("deepslate") || block.Contains("obsidian") || block.Contains("bedrock")) return 0x464553;
        if (block.Contains("blue_")) return 0x4164b5;
        if (block.Contains("yellow_") || block.Contains("gold")) return 0xe6c552;
        if (block.Contains("purple_")) return 0x8958ac;
        if (block.Contains("brick") || block.Contains("terracotta")) return 0xad7660;
        if (block.Contains("cobble")) return 0x8b9087;
        if (block.Contains("stone") || block.Contains("gravel")) return 0x9c9e95;
        return 0x999a95;
    }
}
