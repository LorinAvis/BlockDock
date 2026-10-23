using BlockDock;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

static class WorldFixtures
{
    public sealed record Tags(byte Type, object[] Items);
    public static byte[] Nbt(Dictionary<string, object> data, bool little = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        void Number(long n, int size) { for (int i = 0; i < size; i++) writer.Write((byte)(n >> (8 * (little ? i : size - 1 - i)))); }
        void Text(string s)
        {
            byte[] bytes;
            if (little) bytes = Encoding.UTF8.GetBytes(s);
            else { using var utf = new MemoryStream(); foreach (char c in s) { if (c is > '\0' and < '\u0080') utf.WriteByte((byte)c); else if (c < 2048) { utf.WriteByte((byte)(0xc0 | c >> 6)); utf.WriteByte((byte)(0x80 | c & 63)); } else { utf.WriteByte((byte)(0xe0 | c >> 12)); utf.WriteByte((byte)(0x80 | c >> 6 & 63)); utf.WriteByte((byte)(0x80 | c & 63)); } } bytes = utf.ToArray(); }
            Number(bytes.Length, 2); writer.Write(bytes);
        }
        byte Type(object v) => v switch { sbyte or byte => 1, short => 2, int => 3, long => 4, float => 5, double => 6, byte[] => 7, string => 8, Tags => 9, Dictionary<string, object> => 10, int[] => 11, long[] => 12, _ => throw new Exception("Fixture type") };
        void Payload(object value)
        {
            switch (value)
            {
                case sbyte v: writer.Write(unchecked((byte)v)); break;
                case byte v: writer.Write(v); break;
                case short v: Number(v, 2); break;
                case int v: Number(v, 4); break;
                case long v: Number(v, 8); break;
                case float v: Number(BitConverter.SingleToInt32Bits(v), 4); break;
                case double v: Number(BitConverter.DoubleToInt64Bits(v), 8); break;
                case string v: Text(v); break;
                case byte[] v: Number(v.Length, 4); writer.Write(v); break;
                case int[] v: Number(v.Length, 4); foreach (int n in v) Number(n, 4); break;
                case long[] v: Number(v.Length, 4); foreach (long n in v) Number(n, 8); break;
                case Tags v: writer.Write(v.Type); Number(v.Items.Length, 4); foreach (var item in v.Items) Payload(item); break;
                case Dictionary<string, object> v: foreach (var item in v) { writer.Write(Type(item.Value)); Text(item.Key); Payload(item.Value); } writer.Write((byte)0); break;
            }
        }
        writer.Write((byte)10); Text(""); Payload(data); return stream.ToArray();
    }
    public static byte[] Compressed(byte[] data, int kind = 2)
    {
        using var stream = new MemoryStream(); using (Stream zipper = kind == 1 ? new GZipStream(stream, CompressionLevel.Fastest, true) : kind == 4 ? new DeflateStream(stream, CompressionLevel.Fastest, true) : new ZLibStream(stream, CompressionLevel.Fastest, true)) zipper.Write(data); return stream.ToArray();
    }
    public static SavedWorld World(WorldSource source, string name, string label, long seed)
    {
        string folder = Path.Combine(source.Root, name); Directory.CreateDirectory(folder);
        var data = new Dictionary<string, object> { ["LevelName"] = label, ["GameType"] = 0, ["SpawnX"] = 0, ["SpawnZ"] = 0, ["LastPlayed"] = source.Bedrock ? 1_700_000_000L : 1_700_000_000_000L };
        if (source.Bedrock) { data["RandomSeed"] = seed; data["lastOpenedWithVersion"] = new Tags(3, new object[] { 1, 18, 2, 0, 0 }); var raw = Nbt(data, true); var payload = new byte[raw.Length + 8]; BinaryPrimitives.WriteInt32LittleEndian(payload, 10); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), raw.Length); raw.CopyTo(payload, 8); File.WriteAllBytes(Path.Combine(folder, "level.dat"), payload); Directory.CreateDirectory(Path.Combine(folder, "db")); File.WriteAllBytes(Path.Combine(folder, "db/LOCK"), Array.Empty<byte>()); }
        else { data["WorldGenSettings"] = new Dictionary<string, object> { ["seed"] = seed }; data["Version"] = new Dictionary<string, object> { ["Name"] = source.Version }; File.WriteAllBytes(Path.Combine(folder, "level.dat"), Compressed(Nbt(new() { ["Data"] = data }), 1)); File.WriteAllBytes(Path.Combine(folder, "session.lock"), new byte[8]); }
        return WorldLibrary.Read(source, folder);
    }
    public static long[] Packed(int[] indices, int bits, bool dense)
    {
        var words = new ulong[dense ? (indices.Length * bits + 63) / 64 : (indices.Length + 64 / bits - 1) / (64 / bits)];
        for (int i = 0; i < indices.Length; i++)
        {
            int word = dense ? i * bits / 64 : i / (64 / bits), offset = dense ? i * bits % 64 : i % (64 / bits) * bits;
            words[word] |= (ulong)indices[i] << offset; if (dense && offset + bits > 64) words[word + 1] |= (ulong)indices[i] >> (64 - offset);
        }
        return words.Select(v => unchecked((long)v)).ToArray();
    }
    public static void Region(string folder, int rx, int rz, IEnumerable<(int X, int Z, Dictionary<string, object> Root)> chunks, string suffix = "mca")
    {
        Directory.CreateDirectory(folder); using var file = File.Create(Path.Combine(folder, $"r.{rx}.{rz}.{suffix}")); file.Write(new byte[8192]); var header = new byte[4096];
        foreach (var chunk in chunks)
        {
            var data = Compressed(Nbt(chunk.Root)); int sector = (int)(file.Length / 4096), count = (data.Length + 5 + 4095) / 4096, index = (chunk.X & 31) + (chunk.Z & 31) * 32;
            header[index * 4] = (byte)(sector >> 16); header[index * 4 + 1] = (byte)(sector >> 8); header[index * 4 + 2] = (byte)sector; header[index * 4 + 3] = (byte)count;
            file.Position = file.Length; var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length + 1); file.Write(length); file.WriteByte(2); file.Write(data); file.SetLength((sector + count) * 4096L);
        }
        file.Position = 0; file.Write(header);
    }
    public static Dictionary<string, object> Chunk(int cx, int cz, bool demo = true, bool dense = false)
    {
        string[] names = demo ? new[] { "air", "stone", "grass_block", "sand", "water", "oak_planks", "cherry_leaves", "oak_leaves", "spruce_leaves", "cobblestone", "dirt_path", "wheat", "farmland", "spruce_planks", "bricks", "oak_log" } : Enumerable.Range(0, 17).Select(i => i == 0 ? "air" : i == 16 ? "oak_planks" : "stone").ToArray();
        var sections = new List<object>();
        // These are intentionally generated test-world chunks, never a substituted map image.
        var columns = new (int Height, int Surface, int Top, int Cover)[256];
        if (demo) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) columns[z * 16 + x] = DemoColumn(cx * 16 + x, cz * 16 + z);
        foreach (int sy in new[] { 3, 4, 5 })
        {
            var blocks = new int[4096];
            for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
            {
                int wy = sy * 16 + y;
                if (!demo) { blocks[y * 256 + z * 16 + x] = 16; continue; }
                var column = columns[z * 16 + x];
                int id = wy < column.Height ? 1 : wy == column.Height ? column.Surface : wy < 64 ? 4 : 0;
                if (column.Top > column.Height && wy > column.Height && wy <= column.Top)
                    id = column.Cover is 6 or 7 or 8 ? wy >= column.Top - 2 ? column.Cover : 15 : wy == column.Top ? column.Cover : 9;
                blocks[y * 256 + z * 16 + x] = id;
            }
            int bits = demo ? 4 : 5;
            var section = new Dictionary<string, object> { ["Y"] = (sbyte)sy };
            var palette = new Tags(10, names.Select(n => (object)new Dictionary<string, object> { ["Name"] = "minecraft:" + n }).ToArray());
            if (dense) { section["Palette"] = palette; section["BlockStates"] = Packed(blocks, bits, true); }
            else section["block_states"] = new Dictionary<string, object> { ["palette"] = palette, ["data"] = Packed(blocks, bits, false) };
            sections.Add(section);
        }
        var root = new Dictionary<string, object> { [dense ? "Sections" : "sections"] = new Tags(10, sections.ToArray()), ["xPos"] = cx, ["zPos"] = cz };
        return dense ? new() { ["Level"] = root } : root;
    }
    static uint Hash(int x, int z)
    {
        uint h = unchecked((uint)x * 0x8da6b343U ^ (uint)z * 0xd8163841U ^ 0xcb1ab31fU); h ^= h >> 13; h *= 0x85ebca6bU; return h ^ h >> 16;
    }
    static double Noise(double x, double z)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z); double fx = x - ix, fz = z - iz; fx *= fx * (3 - 2 * fx); fz *= fz * (3 - 2 * fz);
        double N(int xx, int zz) => Hash(xx, zz) / (double)uint.MaxValue;
        return (N(ix, iz) * (1 - fx) + N(ix + 1, iz) * fx) * (1 - fz) + (N(ix, iz + 1) * (1 - fx) + N(ix + 1, iz + 1) * fx) * fz;
    }
    static readonly (int X1, int Z1, int X2, int Z2, int Height, int Roof)[] Houses =
    {
        (16,16,40,40,72,5), (-5,15,8,27,70,13), (44,-5,59,9,73,14),
        (58,25,74,39,71,13), (15,-30,29,-16,71,5), (83,7,94,19,70,14)
    };
    static (int Height, int Surface, int Top, int Cover) DemoColumn(int x, int z)
    {
        double coast = 64 + 10 * Math.Sin(x / 32.0) + 15 * (Noise(x / 41.0, 2) - .5);
        double riverX = -30 + 13 * Math.Sin(z / 33.0) + 9 * Math.Sin(z / 15.0);
        double river = Math.Abs(x - riverX), water = Math.Max(z - coast, 7 + 3 * Math.Sin(z / 24.0) - river);
        int height = (int)Math.Round(65 + 12 * Noise(x / 53.0, z / 53.0) + 4 * Noise(x / 13.0, z / 13.0));
        if (water > -6) height = (int)Math.Clamp(62 - water * .6, 49, 66);
        bool settlement = x is >= -10 and < 100 && z is >= -39 and < 46;
        if (settlement && water < -6) height = 65;
        int surface = height < 65 ? 3 : 2, top = height, cover = surface;
        // Small individual tree crowns follow an irregular forest edge.
        if (height >= 65 && !settlement && (Noise(x / 43.0, z / 43.0) > .40 || z < -45))
        {
            int cellX = WorldMapRenderer.Floor(x, 11), cellZ = WorldMapRenderer.Floor(z, 11);
            for (int zz = cellZ - 1; zz <= cellZ + 1; zz++) for (int xx = cellX - 1; xx <= cellX + 1; xx++)
            {
                uint h = Hash(xx, zz); if (h % 5 == 0) continue;
                int tx = xx * 11 + (int)(h % 7) + 2, tz = zz * 11 + (int)(h >> 5) % 7 + 2;
                double dx = x - tx, dz = z - tz, d = dx * dx + dz * dz * (1 + (h % 3) * .13);
                if (d > 17 + h % 5 || Noise(tx / 43.0, tz / 43.0) < .40 && tz >= -45) continue;
                int canopy = height + 5 + (int)(h % 3) - (int)(Math.Sqrt(d) / 2);
                if (canopy > top) { top = canopy; cover = tx < -45 && tz < 10 ? 6 : tx > 45 && tz < -30 ? 8 : 7; }
            }
        }
        if (settlement && water < -6 && (Math.Abs(z - (12 + 2 * Math.Sin(x / 18.0))) <= 2 || Math.Abs(x - 11) <= 2)) { surface = 10; cover = 10; }
        if (x is >= 45 and < 78 && z is >= -36 and < -17) { height = 65; surface = (x - 45) % 6 == 0 ? 4 : 12; top = surface == 4 ? 65 : 66; cover = surface == 4 ? 4 : 11; }
        foreach (var house in Houses)
            if (x >= house.X1 && x < house.X2 && z >= house.Z1 && z < house.Z2)
            {
                height = 65; surface = 9; top = house.Height; cover = house.Roof;
                // Other roofs are pitched; the main roof stays flat for the reader's exact-color assertion.
                if (house.X1 != 16) top += Math.Min(x - house.X1, house.X2 - x - 1) / 3;
                return (height, surface, top, cover);
            }
        // A footbridge and a small harbor pier remain actual saved planks, not a drawing overlay.
        if (z is >= -12 and < -8 && x is >= -49 and < -7 || x is >= 42 and < 47 && z is >= 42 and < 99 || z is >= 92 and < 97 && x is >= 31 and < 64)
            return (Math.Min(height, 63), 3, 66, 5);
        return (height, surface, top, cover);
    }
    public static void Demo(SavedWorld world)
    {
        var chunks = new List<(int X, int Z, Dictionary<string, object> Root)>();
        for (int z = -8; z < 8; z++) for (int x = -8; x < 8; x++) chunks.Add((x, z, Chunk(x, z)));
        foreach (var group in chunks.GroupBy(c => (WorldMapRenderer.Floor(c.X, 32), WorldMapRenderer.Floor(c.Z, 32)))) Region(Path.Combine(world.Folder, "region"), group.Key.Item1, group.Key.Item2, group);
    }
    static byte[] Var(ulong n) { using var stream = new MemoryStream(); while (n >= 128) { stream.WriteByte((byte)(n | 128)); n >>= 7; } stream.WriteByte((byte)n); return stream.ToArray(); }
    static byte[] Slice(byte[] b) => Var((ulong)b.Length).Concat(b).ToArray();
    public static byte[] Physical(byte[] record)
    {
        using var stream = new MemoryStream(); int position = 0; bool first = true;
        do
        {
            int available = 32768 - (int)(stream.Length % 32768); if (available < 7) { stream.Write(new byte[available]); available = 32768; }
            int length = Math.Min(record.Length - position, available - 7); bool last = position + length == record.Length;
            byte kind = first ? last ? (byte)1 : (byte)2 : last ? (byte)4 : (byte)3;
            var payload = new[] { kind }.Concat(record.AsSpan(position, length).ToArray()).ToArray(); var header = new byte[7];
            BinaryPrimitives.WriteUInt32LittleEndian(header, BedrockWorldReader.MaskedCrc(payload)); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)length); header[6] = kind;
            stream.Write(header); stream.Write(record, position, length); position += length; first = false;
        } while (position < record.Length);
        return stream.ToArray();
    }
    public static byte[] BedrockSection(string block = "grass_block", int bits = 1)
    {
        var bytes = new List<byte> { 9, 1, 4, (byte)(bits * 2) };
        if (bits > 0)
        {
            int per = 32 / bits; var words = new uint[(4096 + per - 1) / per];
            for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) { int i = x * 256 + z * 16; words[i / per] |= 1U << (i % per * bits); }
            foreach (uint word in words) bytes.AddRange(BitConverter.GetBytes(word));
            bytes.AddRange(BitConverter.GetBytes(2)); bytes.AddRange(Nbt(new() { ["name"] = "minecraft:air" }, true));
        }
        else bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(Nbt(new() { ["name"] = "minecraft:" + block }, true)); return bytes.ToArray();
    }
    public static byte[] Key(int cx = 0, int cz = 0) => BitConverter.GetBytes(cx).Concat(BitConverter.GetBytes(cz)).Concat(new byte[] { 47, 4 }).ToArray();
    public static byte[] Batch(ulong sequence, params (byte[] Key, byte[]? Value)[] records)
    {
        using var stream = new MemoryStream(); stream.Write(BitConverter.GetBytes(sequence)); stream.Write(BitConverter.GetBytes(records.Length));
        foreach (var record in records) { stream.WriteByte(record.Value == null ? (byte)0 : (byte)1); stream.Write(Slice(record.Key)); if (record.Value != null) stream.Write(Slice(record.Value)); }
        return stream.ToArray();
    }
    public static void Database(SavedWorld world, byte[] value, bool latest = true)
    {
        string folder = Path.Combine(world.Folder, "db"); Directory.CreateDirectory(folder); byte[] key = Key(), internalKey = key.Concat(BitConverter.GetBytes((5UL << 8) | 1)).ToArray();
        byte[] DataBlock(byte[] k, byte[] v) => Var(0).Concat(Var((ulong)k.Length)).Concat(Var((ulong)v.Length)).Concat(k).Concat(v).Concat(new byte[4]).Concat(BitConverter.GetBytes(1)).ToArray();
        using var table = new MemoryStream();
        (ulong Offset, ulong Length) WriteBlock(byte[] raw, byte compression)
        {
            byte[] content = compression == 4 ? Compressed(raw, 4) : raw; var all = content.Concat(new[] { compression }).ToArray(); ulong start = (ulong)table.Position;
            table.Write(all); table.Write(BitConverter.GetBytes(BedrockWorldReader.MaskedCrc(all))); return (start, (ulong)content.Length);
        }
        var data = WriteBlock(DataBlock(internalKey, value), 4); var meta = WriteBlock(new byte[4].Concat(BitConverter.GetBytes(1)).ToArray(), 0);
        var index = WriteBlock(DataBlock(internalKey, Var(data.Offset).Concat(Var(data.Length)).ToArray()), 0);
        var footer = Var(meta.Offset).Concat(Var(meta.Length)).Concat(Var(index.Offset)).Concat(Var(index.Length)).ToList(); while (footer.Count < 40) footer.Add(0); footer.AddRange(BitConverter.GetBytes(0xdb4775248b80fb57UL)); table.Write(footer.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "000001.ldb"), table.ToArray()); File.WriteAllBytes(Path.Combine(folder, "000002.ldb"), new byte[] { 99 });
        var manifest = new List<byte>(); manifest.Add(2); manifest.AddRange(Var(3));
        foreach (int number in new[] { 2, 1 }) { manifest.Add(7); manifest.AddRange(Var(0)); manifest.AddRange(Var((ulong)number)); manifest.AddRange(Var((ulong)table.Length)); manifest.AddRange(Slice(internalKey)); manifest.AddRange(Slice(internalKey)); }
        manifest.Add(6); manifest.AddRange(Var(0)); manifest.AddRange(Var(2));
        File.WriteAllBytes(Path.Combine(folder, "MANIFEST-000004"), Physical(manifest.ToArray())); File.WriteAllText(Path.Combine(folder, "CURRENT"), "MANIFEST-000004\n");
        if (latest) File.WriteAllBytes(Path.Combine(folder, "000003.log"), Physical(Batch(100, (key, null), (key, BedrockSection("oak_planks")))));
    }
}
