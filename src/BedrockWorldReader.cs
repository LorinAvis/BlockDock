using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockDock;

// Read-only SST/WAL reader. Format references: Google's LevelDB docs and
// Mojang minecraft-creator-tools (MIT); no native database is opened or repaired.
public sealed class BedrockWorldReader
{
    public sealed record Entry(byte[] Key, byte[]? Value, ulong Sequence);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly Func<byte[], bool> wanted;
    readonly CancellationToken ct;
    static readonly uint[] CrcTable = CreateCrcTable();
    long retained;
    public BedrockWorldReader(Func<byte[], bool> wanted, CancellationToken ct) { this.wanted = wanted; this.ct = ct; }
    public IReadOnlyCollection<Entry> Read(string folder, IProgress<string>? progress)
    {
        entries.Clear(); retained = 0; ct.ThrowIfCancellationRequested();
        WorldFiles.NoLinks(folder);
        string currentPath = Path.Combine(folder, "CURRENT");
        if (!File.Exists(currentPath)) throw new IOException("存档缺少数据库目录，请先在游戏中完整保存一次。");
        string manifest = Encoding.UTF8.GetString(FileBytes(currentPath, 1024)).Trim();
        if (!Regex.IsMatch(manifest, @"^MANIFEST-\d+$", RegexOptions.CultureInvariant)) throw new InvalidDataException("存档数据库清单无效。");
        var tables = new HashSet<ulong>(); ulong logNumber = 0, previousLog = 0;
        foreach (byte[] record in LogRecords(FileBytes(Path.Combine(folder, manifest)), ct))
        {
            var reader = new Bytes(record);
            while (reader.Left > 0)
                switch (reader.Var())
                {
                    case 1: reader.Slice(); break;
                    case 2: logNumber = reader.Var(); break;
                    case 3: case 4: reader.Var(); break;
                    case 5: reader.Var(); reader.Slice(); break;
                    case 6: reader.Var(); tables.Remove(reader.Var()); break;
                    case 7: reader.Var(); ulong file = reader.Var(); reader.Var(); reader.Slice(); reader.Slice(); tables.Add(file); break;
                    case 9: previousLog = reader.Var(); break;
                    default: throw new InvalidDataException("暂不支持此数据库清单版本。");
                }
        }
        foreach (ulong table in tables.Order())
        {
            ct.ThrowIfCancellationRequested(); string name = table.ToString("D6");
            string path = Path.Combine(folder, name + ".ldb"); if (!File.Exists(path)) path = Path.Combine(folder, name + ".sst");
            progress?.Report("读取地图 · " + Path.GetFileName(path)); Table(FileBytes(path));
        }
        foreach (string path in Directory.EnumerateFiles(folder, "*.log").Order())
        {
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(path), out ulong number) || number < logNumber && number != previousLog) continue;
            ct.ThrowIfCancellationRequested(); progress?.Report("读取最近保存的区块…");
            foreach (byte[] record in LogRecords(FileBytes(path), ct))
            {
                var reader = new Bytes(record); ulong sequence = reader.U64(); uint count = reader.U32();
                const ulong maxSequence = (1UL << 56) - 1;
                if (count > 5_000_000 || sequence > maxSequence || (ulong)count > maxSequence - sequence + 1) throw new InvalidDataException("数据库日志序列无效。");
                for (uint i = 0; i < count; i++)
                {
                    int kind = reader.Byte(); byte[] key = reader.Slice();
                    if (kind is not (0 or 1)) throw new InvalidDataException("数据库日志记录无效。");
                    byte[]? value = kind == 1 ? reader.Slice() : null; Add(key, value, checked(sequence + i));
                }
                if (reader.Left != 0) throw new InvalidDataException("数据库日志长度不匹配。");
            }
        }
        return entries.Values.ToArray();
    }
    void Add(byte[] key, byte[]? value, ulong sequence)
    {
        ct.ThrowIfCancellationRequested(); if (!wanted(key)) return;
        string id = Convert.ToHexString(key);
        if (entries.TryGetValue(id, out var existing) && existing.Sequence >= sequence) return;
        retained += (value?.Length ?? 0) - (existing?.Value?.Length ?? 0) + (existing == null ? 128L + key.Length * 5L : 0);
        if (retained > 256 * 1024 * 1024) throw new IOException("地图范围过大，请缩小范围后重新查看。");
        entries[id] = new(key, value, sequence);
    }
    byte[] FileBytes(string path, int limit = 256 * 1024 * 1024)
    {
        WorldFiles.NoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return WorldNbt.ReadLimited(input, limit, ct);
    }
    void Table(byte[] data)
    {
        if (data.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(data.Length - 8)) != 0xdb4775248b80fb57UL) throw new InvalidDataException("区块数据库表不完整。");
        var footer = new Bytes(data, data.Length - 48); footer.Var(); footer.Var(); ulong indexOffset = footer.Var(), indexSize = footer.Var();
        foreach (var (_, handle) in BlockEntries(Block(data, indexOffset, indexSize, ct), ct))
        {
            ct.ThrowIfCancellationRequested(); var location = new Bytes(handle); ulong offset = location.Var(), size = location.Var();
            foreach (var (internalKey, value) in BlockEntries(Block(data, offset, size, ct), ct))
            {
                if (internalKey.Length < 8) throw new InvalidDataException();
                ulong trailer = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
                byte kind = (byte)trailer; if (kind > 1) throw new InvalidDataException();
                Add(internalKey[..^8], kind == 0 ? null : value, trailer >> 8);
            }
        }
    }
    static byte[] Block(byte[] file, ulong offset, ulong size, CancellationToken ct)
    {
        if (offset > (ulong)file.Length || size > WorldNbt.Limit || size + 5 > (ulong)file.Length - offset) throw new InvalidDataException("数据库区块越界。");
        int start = (int)offset, length = (int)size; byte compression = file[start + length];
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + length + 1));
        if (MaskedCrc(file.AsSpan(start, length + 1), ct) != checksum) throw new InvalidDataException("数据库区块校验失败，原存档未修改。");
        byte[] compressed = file.AsSpan(start, length).ToArray();
        return compression switch { 0 => compressed, 1 => Snappy(compressed, ct), 2 => WorldNbt.Inflate(compressed, 2, ct), 4 => WorldNbt.Inflate(compressed, 4, ct), _ => throw new InvalidDataException("暂不支持此基岩版压缩格式。") };
    }
    static IEnumerable<(byte[] Key, byte[] Value)> BlockEntries(byte[] data, CancellationToken ct)
    {
        if (data.Length < 4) throw new InvalidDataException();
        uint restarts = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
        if (restarts == 0 || restarts > (data.Length - 4) / 4) throw new InvalidDataException();
        int end = data.Length - 4 - (int)restarts * 4; var reader = new Bytes(data, 0, end); byte[] prior = Array.Empty<byte>();
        // Restart offsets are part of the table integrity, even when reading sequentially.
        uint previous = 0;
        for (int i = 0; i < restarts; i++)
        {
            uint at = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + i * 4));
            if (i == 0 && at != 0 || i > 0 && at <= previous || at > end || at == end && end != 0) throw new InvalidDataException("数据库区块索引无效。");
            previous = at;
        }
        int restartIndex = 0;
        while (reader.Left > 0)
        {
            ct.ThrowIfCancellationRequested(); int position = reader.Position;
            int common = reader.Count(), additional = reader.Count(), length = reader.Count();
            if (restartIndex < restarts)
            {
                uint at = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + restartIndex * 4));
                if (at < position || at == position && common != 0) throw new InvalidDataException("数据库区块索引无效。");
                if (at == position) restartIndex++;
            }
            if (common > prior.Length || additional > 1024 * 1024 - common) throw new InvalidDataException();
            var key = new byte[common + additional]; prior.AsSpan(0, common).CopyTo(key); reader.Take(additional).CopyTo(key.AsSpan(common));
            var value = reader.Take(length).ToArray(); prior = key; yield return (key, value);
        }
        if (end > 0 && restartIndex != restarts) throw new InvalidDataException("数据库区块索引不完整。");
    }
    public static IEnumerable<byte[]> LogRecords(byte[] data, CancellationToken ct = default)
    {
        int offset = 0; MemoryStream? pending = null;
        try
        {
            while (offset + 7 <= data.Length)
            {
                ct.ThrowIfCancellationRequested();
                int remaining = 32768 - offset % 32768; if (remaining < 7) { offset += remaining; continue; }
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4)); byte kind = data[offset + 6];
                if (kind == 0 && length == 0) { offset += remaining; continue; }
                if (length > remaining - 7) throw new InvalidDataException("数据库日志边界无效。");
                if (offset + 7 + length > data.Length) yield break; // An interrupted final write is not a complete record.
                if (MaskedCrc(data.AsSpan(offset + 6, length + 1), ct) != checksum) throw new InvalidDataException("数据库日志校验失败。");
                byte[] part = data.AsSpan(offset + 7, length).ToArray(); offset += length + 7;
                if (kind == 1) { pending?.Dispose(); pending = null; yield return part; }
                else if (kind == 2) { pending?.Dispose(); pending = new(); pending.Write(part); }
                else if (kind is 3 or 4 && pending != null)
                {
                    if (pending.Length + part.Length > WorldNbt.Limit) throw new InvalidDataException("数据库记录过大。");
                    pending.Write(part); if (kind == 4) { var record = pending.ToArray(); pending.Dispose(); pending = null; yield return record; }
                }
                else throw new InvalidDataException("数据库日志片段不完整。");
            }
        }
        finally { pending?.Dispose(); }
    }
    static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++) { uint crc = value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78U & (uint)-(int)(crc & 1)); table[value] = crc; }
        return table;
    }
    public static uint MaskedCrc(ReadOnlySpan<byte> bytes, CancellationToken ct = default)
    {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++) { if ((i & 4095) == 0) ct.ThrowIfCancellationRequested(); crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8); }
        crc = ~crc; return ((crc >> 15) | (crc << 17)) + 0xa282ead8U;
    }
    public static byte[] Snappy(byte[] data, CancellationToken ct = default)
    {
        var reader = new Bytes(data); int size = reader.Count(); if (size > WorldNbt.Limit) throw new InvalidDataException();
        var output = new byte[size]; int written = 0;
        while (reader.Left > 0 && written < size)
        {
            ct.ThrowIfCancellationRequested();
            int tag = reader.Byte(), kind = tag & 3, length, distance = 0;
            if (kind == 0)
            {
                length = tag >> 2;
                if (length < 60) length++;
                else { int width = length - 59; uint count = 0; for (int i = 0; i < width; i++) count |= (uint)reader.Byte() << (8 * i); if (count >= int.MaxValue) throw new InvalidDataException(); length = (int)count + 1; }
                if (length > size - written) throw new InvalidDataException(); reader.Take(length).CopyTo(output.AsSpan(written));
            }
            else
            {
                if (kind == 1) { length = 4 + ((tag >> 2) & 7); distance = ((tag & 0xe0) << 3) | reader.Byte(); }
                else { length = 1 + (tag >> 2); uint value = kind == 2 ? (uint)(reader.Byte() | (reader.Byte() << 8)) : reader.U32(); if (value > int.MaxValue) throw new InvalidDataException(); distance = (int)value; }
                if (distance <= 0 || distance > written || length > size - written) throw new InvalidDataException();
                for (int i = 0; i < length; i++) output[written + i] = output[written + i - distance];
            }
            written += length;
        }
        if (written != size || reader.Left != 0) throw new InvalidDataException("Snappy 数据不完整。"); return output;
    }
    sealed class Bytes(byte[] data, int offset = 0, int? end = null)
    {
        int position = offset;
        public int Position => position;
        public int Left => (end ?? data.Length) - position;
        public ReadOnlySpan<byte> Take(int count) { if (count < 0 || count > Left) throw new InvalidDataException("数据库数据不完整。"); var b = data.AsSpan(position, count); position += count; return b; }
        public byte Byte() => Take(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public ulong Var() { ulong value = 0; for (int shift = 0; shift < 64; shift += 7) { byte b = Byte(); if (shift == 63 && b > 1) throw new InvalidDataException(); value |= (ulong)(b & 127) << shift; if (b < 128) return value; } throw new InvalidDataException(); }
        public int Count() { ulong value = Var(); if (value > int.MaxValue) throw new InvalidDataException(); return (int)value; }
        public byte[] Slice() => Take(Count()).ToArray();
    }
}
