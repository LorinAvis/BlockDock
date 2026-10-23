using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace BlockDock;

public sealed record WorldSource(string Name, string Version, bool Bedrock, string Root)
{
    public string Edition => Bedrock ? "基岩版" : "Java";
    public string Label => Edition + " · " + Name;
    public override string ToString() => Label;
}
public sealed record SavedWorld(WorldSource Source, string Folder, string Name, long? Seed, string Version,
    DateTimeOffset? Played, int SpawnX, int SpawnZ, string Mode, string Warning = "")
{
    public string SeedText => Seed?.ToString(CultureInfo.InvariantCulture) ?? "未能读取";
    public string Summary => Source.Label + ((Version.Length > 0 && !Source.Name.Contains(Version, StringComparison.OrdinalIgnoreCase)) ? " · " + Version : "");
    public string PlayedText => Played?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
    public string IconPath => Path.Combine(Folder, Source.Bedrock ? "world_icon.jpeg" : "icon.png");
}

public static class WorldLibrary
{
    public static List<WorldSource> Sources(Settings settings, IEnumerable<GameInstance> games)
    {
        var sources = games.Select(g => new WorldSource(g.Name, g.Minecraft, false, Path.Combine(g.GameDirectory, "saves"))).ToList();
        if (!sources.Any(s => WorldFiles.Same(s.Root, Path.Combine(settings.JavaRoot, "saves")))) sources.Add(new("共享存档目录", "", false, Path.Combine(settings.JavaRoot, "saves")));
        if (Directory.Exists(BedrockInstances.Root))
            foreach (string identity in WorldFiles.Directories(BedrockInstances.Root).Where(p => Path.GetFileName(p).StartsWith("Microsoft.Minecraft", StringComparison.Ordinal)))
                foreach (string version in WorldFiles.Directories(identity))
                    foreach (string kind in WorldFiles.Directories(version).Where(p => Path.GetFileName(p) is "uwp" or "gdk"))
                        AddBedrock(sources, Path.Combine(kind, "user-data"), Path.GetFileName(version) + (identity.Contains("Beta") ? " Preview" : "") + " · " + Path.GetFileName(kind), Path.GetFileName(version), Path.GetFileName(kind) == "gdk");
        foreach (string family in new[] { "Microsoft.MinecraftUWP_8wekyb3d8bbwe", "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe" })
            foreach (bool gdk in new[] { false, true })
                try
                {
                    string live = BedrockInstances.LiveDirectory(family, gdk);
                    if (!gdk)
                    {
                        string localState = Directory.GetParent(Directory.GetParent(live)!.FullName)!.FullName;
                        live = new BedrockSystemPath(localState).Resolve(live);
                    }
                    if (Directory.Exists(live))
                    {
                        var directory = new DirectoryInfo(live);
                        if (directory.LinkTarget != null) live = directory.ResolveLinkTarget(true)!.FullName;
                        AddBedrock(sources, live, "系统" + (family.Contains("Beta") ? " Preview" : "") + (gdk ? " · GDK" : " · UWP"), "", gdk);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return sources.GroupBy(s => Path.GetFullPath(s.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with { Name = string.Join(" / ", g.Select(s => s.Name).Distinct()) }).ToList();
    }
    static void AddBedrock(List<WorldSource> list, string data, string name, string version, bool gdk)
    {
        var candidates = new List<string> { Path.Combine(data, "minecraftWorlds"), Path.Combine(data, "games", "com.mojang", "minecraftWorlds") };
        string users = Path.Combine(data, "Users");
        if (Directory.Exists(users)) foreach (string user in WorldFiles.Directories(users).Take(100)) candidates.Add(Path.Combine(user, "games", "com.mojang", "minecraftWorlds"));
        var existing = candidates.Where(Directory.Exists).ToList();
        if (existing.Count == 0) existing.Add(gdk ? Path.Combine(users, "Shared", "games", "com.mojang", "minecraftWorlds") : candidates[0]);
        list.AddRange(existing.Select(p => new WorldSource(name, version, true, p)));
    }
    public static List<SavedWorld> Scan(IEnumerable<WorldSource> sources, CancellationToken ct)
    {
        var result = new List<SavedWorld>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            foreach (string folder in WorldFiles.Directories(source.Root).Where(p => !Path.GetFileName(p).StartsWith('.')))
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(folder, "level.dat")) || File.Exists(Path.Combine(folder, "level.dat_old"))) result.Add(Read(source, folder));
            }
        }
        return result.OrderByDescending(w => w.Played).ThenBy(w => w.Name).ToList();
    }
    public static SavedWorld Read(WorldSource source, string folder)
    {
        string warning = "";
        Dictionary<string, object>? data = null;
        foreach (string file in new[] { "level.dat", "level.dat_old" })
        {
            try { data = WorldNbt.Level(Path.Combine(folder, file), source.Bedrock); if (file.EndsWith("_old")) warning = "当前元数据不可读，显示上次保存的信息。"; break; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { warning = "元数据读取失败：" + e.Message; }
        }
        if (data == null) return new(source, folder, Path.GetFileName(folder), null, source.Version, null, 0, 0, "未知", warning);
        string name = WorldNbt.Text(data, "LevelName");
        if (name.Length == 0) name = Path.GetFileName(folder);
        long? seed = WorldNbt.Number(data, "RandomSeed") ?? WorldNbt.Number(data, "Seed");
        if (!source.Bedrock && WorldNbt.GetCompound(data, "WorldGenSettings") is { } generator) seed = WorldNbt.Number(generator, "seed") ?? seed;
        string version = source.Bedrock ? string.Join('.', WorldNbt.List(data, "lastOpenedWithVersion").Take(3)) : WorldNbt.GetCompound(data, "Version") is { } info ? WorldNbt.Text(info, "Name") : "";
        DateTimeOffset? played = null; long? time = WorldNbt.Number(data, "LastPlayed");
        if (time != null) try { played = source.Bedrock ? DateTimeOffset.FromUnixTimeSeconds(time.Value) : DateTimeOffset.FromUnixTimeMilliseconds(time.Value); } catch (ArgumentOutOfRangeException) { }
        string mode = WorldNbt.Number(data, "GameType") switch { 0 => "生存", 1 => "创造", 2 => "冒险", 3 or 6 => "旁观", _ => "未知" };
        return new(source, folder, name, seed, version, played, (int)(WorldNbt.Number(data, "SpawnX") ?? 0), (int)(WorldNbt.Number(data, "SpawnZ") ?? 0), mode, warning);
    }
}

public static class WorldFiles
{
    public static bool Same(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    public static void NoLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("存档包含链接目录或文件，请在资源管理器中管理：" + part);
    }
    public static IEnumerable<string> Directories(string root)
    {
        try { NoLinks(root); return Directory.Exists(root) ? Directory.GetDirectories(root).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray() : Array.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
    public static List<string> Tree(string folder, CancellationToken ct)
    {
        NoLinks(folder); var stack = new Stack<string>(); stack.Push(folder); var files = new List<string>();
        while (stack.Count > 0)
            foreach (string item in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                ct.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("存档包含链接，已停止操作。");
                if ((attributes & FileAttributes.Directory) != 0) stack.Push(item); else files.Add(item);
                if (files.Count + stack.Count > 500_000) throw new IOException("存档文件数量过多。");
            }
        return files;
    }
    public static IDisposable Lease(SavedWorld world)
    {
        NoLinks(world.Folder);
        if (!Same(Path.GetDirectoryName(world.Folder)!, world.Source.Root)) throw new IOException("存档不在所选版本的目录中。");
        string lockFile = Path.Combine(world.Folder, world.Source.Bedrock ? "db/LOCK" : "session.lock");
        try { return File.Exists(lockFile) ? new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.Delete) : new MemoryStream(); }
        catch (IOException) { throw new IOException("这个世界正在被游戏或其他工具使用，请退出后重试。"); }
    }
    static string SafeName(string name)
    {
        string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (clean.Length > 70) clean = clean[..70]; return string.IsNullOrWhiteSpace(clean) ? "World" : Paths.Id(clean);
    }
    static string Destination(WorldSource target, string name)
    {
        NoLinks(target.Root); string clean = SafeName(name), path = Paths.Safe(target.Root, clean);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Paths.Safe(target.Root, clean + " (" + i + ")");
        return path;
    }
    static void Cleanup(string stage, string root)
    {
        if (!Same(Path.GetDirectoryName(stage)!, root) || !Path.GetFileName(stage).StartsWith(".blockdock-")) throw new IOException("临时目录校验失败。");
        if (Directory.Exists(stage)) { NoLinks(stage); Tree(stage, default); Directory.Delete(stage, true); }
    }
    static void Transfer(Stream input, Stream output, long length, string label, IProgress<string>? progress, CancellationToken ct, IncrementalHash? hash = null)
    {
        var buffer = new byte[128 * 1024]; long copied = 0; var report = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break;
            if (count > length - copied) throw new IOException("文件长度与记录不符，已停止操作。原存档已保留。");
            ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); hash?.AppendData(buffer, 0, count); copied += count;
            if (report.ElapsedMilliseconds >= 250) { progress?.Report($"{label} · {copied / 1048576d:0.0} / {length / 1048576d:0.0} MB"); report.Restart(); }
        }
        ct.ThrowIfCancellationRequested();
        if (copied != length) throw new IOException("文件在读取期间发生了变化，已停止操作。原存档已保留。");
    }
    static byte[] HashFile(Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024];
        while (true) { ct.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break; hash.AppendData(buffer, 0, count); }
        ct.ThrowIfCancellationRequested(); return hash.GetHashAndReset();
    }
    public static string Copy(SavedWorld world, WorldSource target, IProgress<string>? progress, CancellationToken ct)
    {
        if (world.Source.Bedrock != target.Bedrock) throw new IOException("Java 与基岩版存档格式不同，不能直接互相复制。");
        if (Same(world.Source.Root, target.Root)) throw new IOException("请选择另一个存档目录。");
        return CopyDirectory(world.Folder, target, world.Name, progress, ct);
    }
    public static string CopyDirectory(string folder, WorldSource target, string name, IProgress<string>? progress, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder); string root = Path.GetFullPath(target.Root);
        if (Same(root, folder) || root.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("目标目录不能位于待导入世界中。");
        var source = WorldLibrary.Read(target with { Root = Path.GetDirectoryName(folder)! }, folder);
        if (source.Seed == null && source.Warning.Length > 0) throw new IOException(source.Warning);
        using var lease = Lease(source);
        NoLinks(root); var files = Tree(folder, ct); Directory.CreateDirectory(root);
        string final = Destination(target, name), stage = Paths.Safe(root, ".blockdock-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            foreach (string input in files)
            {
                ct.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(folder, input);
                if (relative is "session.lock" or "db\\LOCK" or "db/LOCK") continue;
                progress?.Report("复制 · " + relative); string output = Paths.Safe(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                NoLinks(input); using var from = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var to = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) Transfer(from, to, from.Length, "复制 · " + relative, progress, ct, hash);
                progress?.Report("校验 · " + relative);
                using (var verify = File.OpenRead(output)) if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), HashFile(verify, ct))) throw new IOException("复制校验失败，原存档已保留。");
                File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(input));
            }
            ct.ThrowIfCancellationRequested(); NoLinks(root); Directory.Move(stage, final); return final;
        }
        finally { Cleanup(stage, root); }
    }
    public static string Export(SavedWorld world, string output, IProgress<string>? progress, CancellationToken ct)
    {
        using var lease = Lease(world); NoLinks(output);
        if (Path.GetFullPath(output).StartsWith(Path.GetFullPath(world.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("请把导出文件保存到世界文件夹以外。");
        if (File.Exists(output)) throw new IOException("目标文件已存在，请换一个名称。");
        var files = Tree(world.Folder, ct); string temp = output + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (string input in files)
                {
                    ct.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(world.Folder, input).Replace('\\', '/');
                    if (relative is "session.lock" or "db/LOCK") continue;
                    progress?.Report("导出 · " + relative); NoLinks(input);
                    using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest); var modified = File.GetLastWriteTime(input);
                    if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
                    using var destination = entry.Open(); Transfer(source, destination, source.Length, "导出 · " + relative, progress, ct);
                }
            ct.ThrowIfCancellationRequested(); File.Move(temp, output); return output;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Import(string archive, WorldSource target, IProgress<string>? progress, CancellationToken ct)
    {
        NoLinks(archive); NoLinks(target.Root); Directory.CreateDirectory(target.Root);
        string stage = Paths.Safe(target.Root, ".blockdock-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage); using var zip = ZipFile.OpenRead(archive); long bytes = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count > 500_000) throw new IOException("压缩包文件数量过多。");
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested(); string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Where(p => p.Length > 0).Any(p => { try { Paths.Id(p); return false; } catch (IOException) { return true; } })) throw new IOException("压缩包包含不安全的路径。");
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new IOException("压缩包包含链接。");
                if (name.EndsWith('/')) continue;
                if (!names.Add(name) || (bytes = checked(bytes + entry.Length)) > 256L * 1024 * 1024 * 1024) throw new IOException("压缩包包含重复文件或数据量过大。");
                string file = Paths.Safe(stage, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); progress?.Report("解压 · " + name);
                using var input = entry.Open();
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) Transfer(input, output, entry.Length, "解压 · " + name, progress, ct);
                File.SetLastWriteTimeUtc(file, entry.LastWriteTime.UtcDateTime);
            }
            var worlds = new List<string>(); if (File.Exists(Path.Combine(stage, "level.dat"))) worlds.Add(stage);
            worlds.AddRange(Directories(stage).Where(p => File.Exists(Path.Combine(p, "level.dat"))));
            if (worlds.Count != 1) throw new IOException("请选择只包含一个世界的 ZIP 或 .mcworld 文件。");
            string world = worlds[0];
            var info = WorldLibrary.Read(target, world); if (info.Warning.Length > 0) throw new IOException("存档格式与目标版本不符，或元数据损坏：" + info.Warning);
            string final = Destination(target, info.Name); ct.ThrowIfCancellationRequested(); NoLinks(target.Root); Directory.Move(world, final); return final;
        }
        finally { Cleanup(stage, target.Root); }
    }
    public static string Recycle(SavedWorld world)
    {
        using var lease = Lease(world); string root = Paths.Safe(world.Source.Root, ".blockdock-recycle/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        NoLinks(root); Directory.CreateDirectory(root); string destination = Paths.Safe(root, Path.GetFileName(world.Folder));
        Directory.Move(world.Folder, destination); return destination;
    }
}
