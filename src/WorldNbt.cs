using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BlockDock;

// Read-only NBT: never rewrites level.dat or opens a world database for writing.
public sealed class WorldNbt
{
    public const int Limit = 32 * 1024 * 1024;
    readonly byte[] bytes;
    readonly bool little;
    int position, tags;
    public int Position => position;
    public WorldNbt(byte[] bytes, bool little = false, int offset = 0)
    { if (bytes.Length > Limit) throw new InvalidDataException("存档元数据过大。"); if (offset < 0 || offset > bytes.Length) throw new InvalidDataException("存档元数据位置无效。"); this.bytes = bytes; this.little = little; position = offset; }
    public Dictionary<string, object> Root()
    {
        if (Byte() != 10) throw new InvalidDataException("存档 NBT 缺少根节点。");
        String(); return Compound(0);
    }
    public static Dictionary<string, object> Level(string path, bool bedrock)
    {
        WorldFiles.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length > Limit) throw new InvalidDataException("存档元数据过大。");
        byte[] data = ReadLimited(file);
        if (bedrock)
        {
            if (data.Length < 11) throw new InvalidDataException("基岩版元数据不完整。");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (size > data.Length - 8 || size < 3) throw new InvalidDataException("基岩版元数据长度不匹配。");
            var reader = new WorldNbt(data.AsSpan(8, (int)size).ToArray(), true); var result = reader.Root();
            if (reader.Position != size) throw new InvalidDataException("基岩版元数据存在未识别的尾部数据。");
            return result;
        }
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
            data = Inflate(data, 1);
        var parser = new WorldNbt(data); var root = parser.Root();
        if (parser.Position != data.Length) throw new InvalidDataException("这不是完整的 Java 版存档元数据。");
        return GetCompound(root, "Data") ?? root;
    }
    public static byte[] ReadLimited(Stream stream, int limit = Limit, CancellationToken ct = default)
    {
        using var result = new MemoryStream(); var buffer = new byte[65536]; int count;
        while (true)
        { ct.ThrowIfCancellationRequested(); count = stream.Read(buffer); if (count == 0) break; if (result.Length + count > limit) throw new InvalidDataException("存档数据超过读取上限。"); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
    public static byte[] Inflate(byte[] data, int compression, CancellationToken ct = default)
    {
        if (compression == 3) return data;
        using var input = new MemoryStream(data);
        using Stream decoder = compression switch { 1 => new GZipStream(input, CompressionMode.Decompress), 2 => new ZLibStream(input, CompressionMode.Decompress), 4 => new DeflateStream(input, CompressionMode.Decompress), _ => throw new InvalidDataException("暂不支持此存档压缩格式。") };
        return ReadLimited(decoder, Limit, ct);
    }
    public static Dictionary<string, object>? GetCompound(Dictionary<string, object> value, string name) => value.GetValueOrDefault(name) as Dictionary<string, object>;
    public static string Text(Dictionary<string, object> value, string name) => value.GetValueOrDefault(name) as string ?? "";
    public static long? Number(Dictionary<string, object> value, string name) => value.GetValueOrDefault(name) switch { long n => n, int n => n, short n => n, sbyte n => n, _ => null };
    public static object[] List(Dictionary<string, object> value, string name) => value.GetValueOrDefault(name) as object[] ?? Array.Empty<object>();
    ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > bytes.Length - position) throw new InvalidDataException("存档数据不完整。");
        var result = bytes.AsSpan(position, count); position += count; return result;
    }
    byte Byte() => Take(1)[0];
    short Short() { var b = Take(2); return little ? BinaryPrimitives.ReadInt16LittleEndian(b) : BinaryPrimitives.ReadInt16BigEndian(b); }
    int Int() { var b = Take(4); return little ? BinaryPrimitives.ReadInt32LittleEndian(b) : BinaryPrimitives.ReadInt32BigEndian(b); }
    long Long() { var b = Take(8); return little ? BinaryPrimitives.ReadInt64LittleEndian(b) : BinaryPrimitives.ReadInt64BigEndian(b); }
    string String()
    {
        int size = unchecked((ushort)Short()); var data = Take(size);
        if (little) return Encoding.UTF8.GetString(data);
        // Java DataInput's modified UTF-8 preserves UTF-16 surrogate pairs.
        var text = new StringBuilder(size);
        for (int i = 0; i < data.Length;)
        {
            int a = data[i++];
            if (a < 128) { text.Append((char)a); continue; }
            if ((a & 0xe0) == 0xc0 && i < data.Length) { int b = data[i++]; if ((b & 0xc0) != 0x80) throw new InvalidDataException(); text.Append((char)(((a & 31) << 6) | (b & 63))); }
            else if ((a & 0xf0) == 0xe0 && i + 1 < data.Length) { int b = data[i++], c = data[i++]; if ((b & 0xc0) != 0x80 || (c & 0xc0) != 0x80) throw new InvalidDataException(); text.Append((char)(((a & 15) << 12) | ((b & 63) << 6) | (c & 63))); }
            else throw new InvalidDataException("存档字符串格式无效。");
        }
        return text.ToString();
    }
    int Count(int width = 1)
    { int count = Int(); if (count < 0 || count > (bytes.Length - position) / width || count > 2_000_000) throw new InvalidDataException("存档数组长度无效。"); return count; }
    Dictionary<string, object> Compound(int depth)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (true) { byte type = Byte(); if (type == 0) return result; string name = String(); result[name] = Value(type, depth + 1); }
    }
    object Value(byte type, int depth)
    {
        if (depth > 128 || ++tags > 2_000_000) throw new InvalidDataException("存档结构过于复杂。");
        switch (type)
        {
            case 1: return unchecked((sbyte)Byte());
            case 2: return Short();
            case 3: return Int();
            case 4: return Long();
            case 5: return BitConverter.Int32BitsToSingle(Int());
            case 6: return BitConverter.Int64BitsToDouble(Long());
            case 7: return Take(Count()).ToArray();
            case 8: return String();
            case 9:
                byte child = Byte(); int count = Count();
                if (child == 0 && count != 0) throw new InvalidDataException();
                var list = new object[count]; for (int i = 0; i < count; i++) list[i] = Value(child, depth + 1); return list;
            case 10: return Compound(depth);
            case 11: int ints = Count(4); var ia = new int[ints]; for (int i = 0; i < ints; i++) ia[i] = Int(); return ia;
            case 12: int longs = Count(8); var la = new long[longs]; for (int i = 0; i < longs; i++) la[i] = Long(); return la;
            default: throw new InvalidDataException("存档包含未知 NBT 类型。");
        }
    }
}
