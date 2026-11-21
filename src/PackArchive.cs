using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlockDock;

public sealed record PackEntry(string ArchivePath, string Path, long Length);

// Format adapters describe files and installer dependencies only. Original launcher
// commands, binaries and version JSON are never installed into the new instance.
public sealed class PackArchive : IDisposable
{
    const long MaximumSize = 10L * 1024 * 1024 * 1024;
    const int MaximumEntries = 100_000, MaximumManifest = 10_000_000;
    readonly Dictionary<string, ZipArchiveEntry> source = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (PackEntry Entry, int Priority)> selected = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> remote = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PackEntry> entries = new();
    readonly CancellationToken cancellation;
    public JsonObject Index { get; private set; } = new();
    public string Format { get; private set; } = "";
    public IReadOnlyList<PackEntry> Entries => entries.AsReadOnly();
    public ZipArchive Archive { get; }
    PackArchive(ZipArchive archive, CancellationToken ct) { Archive = archive; cancellation = ct; }

    public static PackArchive Open(string package, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PackArchive? pack = null;
        try
        {
            pack = new(ZipFile.OpenRead(package), ct); pack.ValidateArchive(); pack.Recognize(Path.GetFileNameWithoutExtension(package)); pack.ValidateTargets(); return pack;
        }
        catch (Exception error)
        {
            pack?.Dispose();
            if (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
                throw new IOException("整合包清单无效，请重新导出完整整合包。", error);
            throw;
        }
    }
    void ValidateArchive()
    {
        if (Archive.Entries.Count > MaximumEntries) throw new IOException("整合包文件数超过 100000 项。");
        long total = 0; var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            bool directory = entry.FullName.EndsWith('/'); string name = directory ? entry.FullName[..^1] : entry.FullName;
            CheckArchivePath(name);
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & 0x400) != 0 || mode is not (0 or 0x8000 or 0x4000) || mode == 0x4000 && !directory || mode == 0x8000 && directory)
                throw new IOException("整合包包含链接或特殊文件：" + name);
            if (!source.TryAdd(name, entry)) throw new IOException("整合包包含重复或仅大小写不同的文件：" + name);
            if (entry.Length < 0 || entry.Length > MaximumSize || entry.Length > MaximumSize - total) throw new IOException("整合包解压规模超过 10 GB。");
            total += entry.Length;
            string[] parts = name.Split('/'); string parent = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (files.Contains(parent)) throw new IOException("整合包的文件与目录冲突：" + parent);
                directories.Add(parent);
            }
            if (directory) { if (files.Contains(name)) throw new IOException("整合包的文件与目录冲突：" + name); directories.Add(name); }
            else { if (directories.Contains(name)) throw new IOException("整合包的文件与目录冲突：" + name); files.Add(name); }
        }
    }
    static void CheckArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Any(char.IsControl))
            throw new IOException("整合包包含不安全的路径。");
        string[] parts = path.Split('/'); if (parts.Length > 64) throw new IOException("整合包目录层级过深。");
        foreach (string part in parts)
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new IOException("整合包包含不安全的路径：" + path);
    }
    bool FileExists(string name) => source.TryGetValue(name, out var entry) && !entry.FullName.EndsWith('/');
    JsonObject Json(string name)
    {
        ReadOnlySpan<byte> bytes = ReadManifest(name);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        return JsonNode.Parse(bytes, null, new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new IOException("整合包清单不是 JSON 对象：" + name);
    }
    byte[] ReadManifest(string name)
    {
        if (!source.TryGetValue(name, out var entry) || entry.FullName.EndsWith('/')) throw new IOException("整合包缺少文件：" + name);
        if (entry.Length > MaximumManifest) throw new IOException("整合包清单超过 10 MB：" + name);
        cancellation.ThrowIfCancellationRequested(); using var input = entry.Open(); using var data = new MemoryStream(); var buffer = new byte[131072];
        int read; while ((read = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (read > MaximumManifest - data.Length || read > entry.Length - data.Length) throw new IOException("整合包清单实际大小超出限制或与目录不符：" + name);
            data.Write(buffer, 0, read);
        }
        if (data.Length != entry.Length) throw new IOException("整合包清单不完整：" + name);
        return data.ToArray();
    }
    static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out string? text) ? text : "";
    static string Required(JsonNode? value, string label) => !string.IsNullOrWhiteSpace(Text(value)) ? Text(value) : throw new IOException("整合包没有提供" + label + "。");
    static JsonArray Array(JsonNode? value, string label) => value as JsonArray ?? throw new IOException("整合包的" + label + "不是有效列表。");
    static bool Flag(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out bool flag) && flag;
    static string Prefix(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    void Recognize(string fallbackName)
    {
        var markers = new List<(string Root, string Kind, string Manifest)>();
        foreach (string name in source.Keys.Where(p => p.Count(c => c == '/') <= 3))
        {
            string file = name[(name.LastIndexOf('/') + 1)..]; string root = Prefix(name);
            if (file.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)) markers.Add((root, "mrpack", name));
            else if (file.Equals("mcbbs.packmeta", StringComparison.OrdinalIgnoreCase)) markers.Add((root, "mcbbs", name));
            else if (file.Equals("modpack.json", StringComparison.OrdinalIgnoreCase) && FileExists(root + "minecraft/pack.json")) markers.Add((root, "hmcl", name));
            else if (file.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase)) markers.Add((root, "multimc", name));
        }
        if (markers.Select(m => m.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) throw new IOException("压缩包包含多个整合包或实例，请只打包一个实例。");
        if (markers.Count > 1) throw new IOException("压缩包同时声明了多种整合包格式，请重新导出单一格式。");
        if (markers.Count == 1)
        {
            var marker = markers[0]; var manifest = Json(marker.Manifest);
            switch (marker.Kind)
            {
                case "mrpack": ReadModrinth(marker.Root, manifest, fallbackName); return;
                case "mcbbs": ReadMcbbs(marker.Root, manifest, fallbackName); return;
                case "hmcl": ReadHmcl(marker.Root, manifest, fallbackName); return;
                case "multimc": ReadMultiMc(marker.Root, manifest, fallbackName); return;
            }
        }
        var curse = source.Keys.Where(p => p.Count(c => c == '/') <= 2 && p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Where(p => { var j = Json(p); return Text(j["manifestType"]) == "minecraftModpack"; }).ToArray();
        if (curse.Length > 1) throw new IOException("压缩包包含多个整合包，请分别导入。");
        if (curse.Length == 1) { ReadCurse(Prefix(curse[0]), Json(curse[0]), fallbackName); return; }
        ReadDirectory(fallbackName);
    }
    void NewIndex(string name, JsonObject dependencies)
    {
        ValidateDependencies(dependencies);
        Index = new() { ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = name, ["dependencies"] = dependencies, ["files"] = new JsonArray() };
    }
    static void AddDependency(JsonObject dependencies, string name, string version)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new IOException("整合包没有提供 " + name + " 的版本。");
        Paths.Id(version);
        if (dependencies[name] != null && Text(dependencies[name]) != version) throw new IOException("整合包声明了互相冲突的 " + name + " 版本。");
        dependencies[name] = version;
    }
    static void ValidateDependencies(JsonObject dependencies)
    {
        Required(dependencies["minecraft"], "Minecraft 版本");
        foreach (var pair in dependencies)
        {
            if (pair.Key is not ("minecraft" or "fabric-loader" or "forge" or "neoforge")) throw new IOException("这个整合包需要尚不支持的加载器：" + pair.Key);
            Paths.Id(Required(pair.Value, pair.Key + " 版本"));
        }
        if (dependencies.Count > 2) throw new IOException("整合包同时声明了多个加载器，请选择兼容的单加载器版本。");
    }
    void ReadModrinth(string root, JsonObject index, string fallbackName)
    {
        if (index["formatVersion"] is not JsonValue format || !format.TryGetValue<int>(out int version) || version != 1 || Text(index["game"]) != "minecraft") throw new IOException("不支持这个 Modrinth 整合包格式。");
        NewIndex(Text(index["name"]) is { Length: > 0 } name ? name : fallbackName, index["dependencies"]?.DeepClone() as JsonObject ?? throw new IOException("整合包没有依赖版本清单。"));
        foreach (var file in Array(index["files"], "文件清单"))
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Required(file?["path"], "文件路径"); ResourceService.CheckPackPath(path);
            if (Text(file?["env"]?["client"]) is "unsupported" or "optional") continue;
            AddRemote(file?.DeepClone() as JsonObject ?? throw new IOException("整合包文件项无效。"));
        }
        AddContent(root + "overrides/", 1, false); AddContent(root + "client-overrides/", 2, false); Format = "Modrinth 整合包";
    }
    void AddRemote(JsonObject file)
    {
        string path = Required(file["path"], "文件路径"); ResourceService.CheckPackPath(path);
        string sha1 = Required(file["hashes"]?["sha1"], "文件 SHA-1 校验值");
        if (!Regex.IsMatch(sha1, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)) throw new IOException("整合包文件校验值无效：" + path);
        if (file["fileSize"] is not JsonValue sizeNode || !sizeNode.TryGetValue<long>(out long size) || size < 0 || size > MaximumSize) throw new IOException("整合包文件大小无效：" + path);
        var downloads = Array(file["downloads"], "下载地址");
        if (downloads.Count is < 1 or > 32) throw new IOException("整合包文件没有有效下载地址：" + path);
        foreach (var value in downloads)
            if (!Uri.TryCreate(Text(value), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
                throw new IOException("整合包文件必须使用有效 HTTPS 下载地址：" + path);
        if (!remote.TryAdd(path, path)) throw new IOException("整合包包含重复文件目标：" + path);
        Index["files"]!.AsArray().Add(file);
    }
    void ReadHmcl(string root, JsonObject manifest, string fallbackName)
    {
        var game = Json(root + "minecraft/pack.json");
        NewIndex(Text(manifest["name"]) is { Length: > 0 } name ? name : fallbackName, GameDependencies(game, Text(manifest["gameVersion"])));
        AddContent(root + "minecraft/", 0, true); AddPackIcon(root); Format = "HMCL 整合包";
    }
    void ReadMcbbs(string root, JsonObject manifest, string fallbackName)
    {
        if (Text(manifest["manifestType"]) != "minecraftModpack") throw new IOException("不支持这个 MCBBS 整合包类型。");
        if (manifest["manifestVersion"] is JsonValue v && (!v.TryGetValue<int>(out int revision) || revision is < 1 or > 2)) throw new IOException("不支持这个 MCBBS 整合包版本。");
        var deps = new JsonObject();
        foreach (var addon in Array(manifest["addons"], "addons")) Component(deps, Required(addon?["id"], "组件编号"), Required(addon?["version"], "组件版本"));
        if (manifest["libraries"] is JsonArray libraries && libraries.Count > 0) throw new IOException("这个整合包包含自定义启动库，尚不能重建，请导出标准加载器整合包。");
        NewIndex(Text(manifest["name"]) is { Length: > 0 } name ? name : fallbackName, deps); AddContent(root + "overrides/", 0, false);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Array(manifest["files"], "文件清单"))
        {
            cancellation.ThrowIfCancellationRequested(); string type = Required(file?["type"], "文件类型");
            if (type == "addon")
            {
                string path = Required(file?["path"], "附加文件路径"); ResourceService.CheckPackPath(path);
                if (!declared.Add(path)) throw new IOException("MCBBS 文件清单重复声明：" + path);
                string hash = Required(file?["hash"], "附加文件 SHA-1");
                if (!Regex.IsMatch(hash, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)) throw new IOException("MCBBS 文件校验值无效：" + path);
                if (!selected.TryGetValue(path, out var local)) throw new IOException("整合包缺少文件 " + path + "，该包需要在线补全。请提供包含全部文件的离线整合包。");
                Verify(local.Entry.ArchivePath, hash);
            }
            else if (type == "curse")
            {
                string filename = Text(file?["fileName"]);
                if (filename.Length == 0) throw new IOException("整合包包含尚未打包的 CurseForge 在线文件。请先在原启动器补全后导出完整离线包。");
                Paths.Id(filename); string path = "mods/" + filename;
                if (!selected.ContainsKey(path)) throw new IOException("整合包缺少 CurseForge 文件 " + filename + "。请先补全并导出完整离线包。");
                if (!declared.Add(path)) throw new IOException("MCBBS 文件清单重复声明：" + path);
            }
            else throw new IOException("尚不支持这种 MCBBS 文件类型：" + type);
        }
        Format = "MCBBS 整合包";
    }
    void ReadMultiMc(string root, JsonObject manifest, string fallbackName)
    {
        if (manifest["formatVersion"] is not JsonValue format || !format.TryGetValue<int>(out int version) || version != 1) throw new IOException("不支持这个 MultiMC 实例格式。");
        var deps = new JsonObject();
        foreach (var component in Array(manifest["components"], "组件清单"))
        {
            if (Flag(component?["disabled"])) continue;
            string id = Required(component?["uid"], "组件编号");
            if (id is "org.lwjgl" or "org.lwjgl3" or "net.fabricmc.intermediary" or "org.quiltmc.hashed") continue;
            Component(deps, id, Required(component?["version"], "组件版本"));
        }
        string name = fallbackName, iconKey = "";
        if (FileExists(root + "instance.cfg"))
        {
            string text = Encoding.UTF8.GetString(ReadManifest(root + "instance.cfg")).TrimStart('\ufeff');
            string configured = Regex.Match(text, @"(?im)^name\s*=\s*(.+)$").Groups[1].Value.Trim(); if (configured.Length > 0) name = configured;
            iconKey = Regex.Match(text, @"(?im)^iconKey\s*=\s*(.+)$").Groups[1].Value.Trim();
            if (Regex.IsMatch(text, @"(?im)^PreLaunchCommand\s*=.*packwiz")) throw new IOException("此实例需要通过启动脚本在线下载整合包。请先导出包含实际模组文件的离线整合包。");
        }
        NewIndex(name, deps);
        bool modern = source.Keys.Any(p => p.StartsWith(root + ".minecraft/", StringComparison.OrdinalIgnoreCase)), legacy = source.Keys.Any(p => p.StartsWith(root + "minecraft/", StringComparison.OrdinalIgnoreCase));
        if (modern && legacy) throw new IOException("实例同时包含 minecraft 和 .minecraft 两个游戏目录，无法确定应导入哪个目录。");
        AddContent(root + (modern ? ".minecraft/" : "minecraft/"), 0, true); AddPackIcon(root, iconKey); Format = "MultiMC / Prism 整合包";
    }
    void ReadCurse(string root, JsonObject manifest, string fallbackName)
    {
        if (manifest["addons"] is JsonArray) { ReadMcbbs(root, manifest, fallbackName); return; }
        if (Array(manifest["files"], "文件清单").Count != 0) throw new IOException("该 CurseForge 整合包还需要下载在线文件。请先在原启动器补全，或导出包含模组文件的离线整合包。");
        var deps = new JsonObject(); AddDependency(deps, "minecraft", Required(manifest["minecraft"]?["version"], "Minecraft 版本"));
        foreach (var loader in manifest["minecraft"]?["modLoaders"] as JsonArray ?? new JsonArray())
        {
            string id = Required(loader?["id"], "加载器版本"); int dash = id.IndexOf('-');
            if (dash <= 0 || dash == id.Length - 1) throw new IOException("整合包加载器编号无效。");
            Component(deps, id[..dash], id[(dash + 1)..]);
        }
        NewIndex(Text(manifest["name"]) is { Length: > 0 } name ? name : fallbackName, deps);
        string folder = Text(manifest["overrides"]) is { Length: > 0 } value ? value : "overrides"; CheckArchivePath(folder);
        AddContent(root + folder.TrimEnd('/') + "/", 0, false); Format = "CurseForge 本地整合包";
    }
    static void Component(JsonObject deps, string id, string version)
    {
        string key = id switch { "game" or "minecraft" or "net.minecraft" => "minecraft", "fabric" or "fabric-loader" or "net.fabricmc.fabric-loader" => "fabric-loader",
            "forge" or "net.minecraftforge" => "forge", "neoforge" or "net.neoforged" => "neoforge", _ => throw new IOException("整合包需要尚不支持的组件：" + id) };
        AddDependency(deps, key, version);
    }
    static bool VanillaId(string id) => Regex.IsMatch(id, @"^(?:\d+\.\d+(?:\.\d+)?(?:-(?:(?:pre|rc)\d+|(?:snapshot|pre|rc)-\d+))?|\d{2}w\d{2}[a-z]|[ab]\d+\.\d+(?:\.\d+)?|rd-\d+)$", RegexOptions.CultureInvariant);
    static JsonObject GameDependencies(JsonObject game, string explicitVersion = "", Func<string, JsonObject?>? resolve = null)
    {
        var deps = new JsonObject(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitVersion.Length > 0) AddDependency(deps, "minecraft", explicitVersion);
        void Metadata(JsonObject node, int depth)
        {
            if (depth > 16) throw new IOException("版本元数据继承层级过深。");
            foreach (string field in new[] { "jar", "inheritsFrom" })
            {
                string id = Text(node[field]); if (id.Length == 0) continue;
                if (VanillaId(id)) AddDependency(deps, "minecraft", id);
                else if (resolve != null && resolve(id) is { } parent)
                {
                    if (!visited.Add(id)) throw new IOException("版本元数据存在循环继承。");
                    Metadata(parent, depth + 1); visited.Remove(id);
                }
            }
            string own = Text(node["id"]);
            if (node["inheritsFrom"] == null && node["jar"] == null && VanillaId(own)) AddDependency(deps, "minecraft", own);
            if (node["patches"] is JsonArray patches) foreach (var patch in patches)
            {
                if (patch is not JsonObject p) throw new IOException("版本补丁清单无效。");
                string id = Required(p["id"], "版本补丁编号");
                Component(deps, id, Required(p["version"], "版本补丁版本")); Metadata(p, depth + 1);
            }
            var args = node["arguments"]?["game"] as JsonArray;
            if (args != null) for (int i = 0; i < args.Count - 1; i++)
            {
                string flag = Text(args[i]), value = Text(args[i + 1]);
                if (flag == "--fml.mcVersion" && value.Length > 0) AddDependency(deps, "minecraft", value);
                if (flag == "--fml.neoForgeVersion" && value.Length > 0) AddDependency(deps, "neoforge", value);
                if (flag == "--fml.forgeVersion" && value.Length > 0)
                {
                    bool neo = deps["neoforge"] != null || Text(node["mainClass"]).StartsWith("net.neoforged.", StringComparison.Ordinal)
                        || args.Any(a => Text(a) == "--fml.neoForgeVersion")
                        || (node["libraries"] as JsonArray)?.Any(l => Text(l?["name"]).StartsWith("net.neoforged", StringComparison.Ordinal)) == true;
                    AddDependency(deps, neo ? "neoforge" : "forge", value);
                }
            }
            foreach (var lib in node["libraries"] as JsonArray ?? new JsonArray())
            {
                string[] coordinate = Text(lib?["name"]).Split(':'); if (coordinate.Length < 3) continue;
                string group = coordinate[0], artifact = coordinate[1], version = coordinate[2];
                if (group == "net.fabricmc" && artifact == "fabric-loader") AddDependency(deps, "fabric-loader", version);
                else if (group == "net.fabricmc" && artifact == "intermediary") AddDependency(deps, "minecraft", version);
                else if ((group == "net.minecraftforge" && artifact is "forge" or "fmlloader" or "minecraftforge") || (group == "net.neoforged" && artifact is "neoforge" or "forge"))
                {
                    string[] parts = version.Split('-'); string loader = version;
                    if (parts.Length >= 2 && VanillaId(parts[0])) { AddDependency(deps, "minecraft", parts[0]); loader = parts[1]; }
                    AddDependency(deps, group == "net.minecraftforge" ? "forge" : "neoforge", loader);
                }
                else if (group.StartsWith("org.quiltmc", StringComparison.Ordinal) || group is "optifine" or "com.mumfrey" or "com.cleanroommc" or "net.legacyfabric")
                    throw new IOException("整合包包含尚不支持的加载器：" + group);
            }
            string main = Text(node["mainClass"]);
            if (depth == 0 && deps.Count == 1 && main.Length > 0 && !main.StartsWith("net.minecraft.", StringComparison.Ordinal)) throw new IOException("无法可靠识别整合包的加载器版本，请导出标准整合包。");
        }
        Metadata(game, 0); ValidateDependencies(deps); return deps;
    }
    void ReadDirectory(string fallbackName)
    {
        var candidates = new List<(string Shared, string Folder, string Id, string Manifest)>();
        foreach (string path in source.Keys.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            string[] parts = path.Split('/');
            if (parts.Length >= 3 && parts.Length <= 9 && parts[^3].Equals("versions", StringComparison.OrdinalIgnoreCase) && parts[^1].Equals(parts[^2] + ".json", StringComparison.OrdinalIgnoreCase))
                candidates.Add((string.Join('/', parts[..^3]) is { Length: > 0 } root ? root + "/" : "", Prefix(path), parts[^2], path));
        }
        if (candidates.Count == 0)
            foreach (string path in source.Keys.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '/') <= 2))
            {
                string root = Prefix(path), id = Path.GetFileNameWithoutExtension(path);
                if (!source.Keys.Any(p => p.StartsWith(root + "mods/", StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "config/", StringComparison.OrdinalIgnoreCase))) continue;
                var json = Json(path); if (Text(json["id"]) != id || (json["inheritsFrom"] == null && json["libraries"] == null && json["mainClass"] == null)) continue;
                candidates.Add((root, root, id, path));
            }
        if (candidates.Count == 0) throw new IOException("未找到可识别的整合包清单或单实例版本 JSON。请使用 HMCL、MCBBS、MultiMC、Modrinth 格式导出，或只压缩一个完整游戏实例。");
        if (candidates.Select(c => c.Shared).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new IOException("压缩包包含多个游戏目录，请只导入一个实例。");
        var metadata = candidates.ToDictionary(c => c.Id, c => Json(c.Manifest), StringComparer.OrdinalIgnoreCase);
        var parents = new HashSet<string>(metadata.Values.SelectMany(j => new[] { Text(j["inheritsFrom"]), Text(j["jar"]) }), StringComparer.OrdinalIgnoreCase);
        var leaves = candidates.Where(c => !parents.Contains(c.Id)).ToArray();
        if (leaves.Length != 1) throw new IOException("压缩包包含多个独立版本或循环继承，请在原启动器中单独导出需要的实例。");
        var chosen = leaves[0];
        NewIndex(chosen.Id.Length > 0 ? chosen.Id : fallbackName, GameDependencies(metadata[chosen.Id], resolve: id => metadata.GetValueOrDefault(id)));
        AddContent(chosen.Shared, 0, true);
        if (!chosen.Folder.Equals(chosen.Shared, StringComparison.OrdinalIgnoreCase)) AddContent(chosen.Folder, 1, true);
        AddPackIcon(chosen.Folder); AddPackIcon(chosen.Shared);
        selected.Remove(chosen.Id + ".json"); Format = "PCL / 单实例目录整合包";
    }
    static bool IgnoredContent(string path)
    {
        string first = path.Split('/')[0];
        if (first.StartsWith(".blockdock-", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("PCL/Logo.png", StringComparison.OrdinalIgnoreCase)) return false;
        if (new[] { "assets", "libraries", "versions", "runtime", "runtimes", "natives", "logs", "crash-reports", "webcache", "cache", "caches", "downloads", "PCL", "PCL2", "HMCL", ".fabric" }.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
        if (path.Contains('/')) return false;
        if (new[] { "pack.json", "modpack.json", "manifest.json", "mcbbs.packmeta", "modrinth.index.json", "mmc-pack.json", "instance.cfg", "launcher_profiles.json", "launcher_accounts.json", "launcher_settings.json", "hmcl_accounts.json", "hmcl.json", "accounts.json", "launcher.cfg", "usercache.json", "usernamecache.json" }.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
        return new[] { ".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".com", ".lnk", ".sh", ".log" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    void AddContent(string prefix, int priority, bool directoryPack)
    {
        foreach (var item in source.Values.Where(e => !e.FullName.EndsWith('/') && e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested(); string path = item.FullName[prefix.Length..];
            if (directoryPack && (IgnoredContent(path) || !path.Contains('/') && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && FileExists(prefix + path[..^4] + ".json"))) continue;
            ResourceService.CheckPackPath(path); var entry = new PackEntry(item.FullName, path, item.Length);
            if (selected.TryGetValue(path, out var old))
            {
                if (!string.Equals(old.Entry.Path, path, StringComparison.Ordinal) || priority <= old.Priority) throw new IOException("整合包包含重复或仅大小写不同的文件目标：" + path);
            }
            selected[path] = (entry, priority);
        }
    }
    void AddPackIcon(string root, string iconKey = "")
    {
        if (new[] { "icon.png", "pack.png", "instance.png", "PCL/Logo.png" }.Any(selected.ContainsKey)) return;
        var names = new List<string> { "icon.png", "pack.png", "instance.png", "PCL/Logo.png" };
        if (iconKey.Length > 0 && iconKey.Length < 256 && iconKey.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && iconKey is not ("." or "..")) names.Add(iconKey + ".png");
        foreach (string name in names)
            if (source.TryGetValue(root + name, out var entry) && !entry.FullName.EndsWith('/') && entry.Length is > 0 and <= 8 * 1024 * 1024)
            { selected["icon.png"] = (new PackEntry(entry.FullName, "icon.png", entry.Length), 0); return; }
    }
    void Verify(string path, string hash)
    {
        using var input = source[path].Open(); using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); var buffer = new byte[131072]; long read = 0;
        int count; while ((count = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); read += count; if (read > source[path].Length) throw new IOException("整合包文件展开大小与清单不符：" + path); sha1.AppendData(buffer, 0, count); }
        if (read != source[path].Length || !Convert.ToHexString(sha1.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("整合包文件校验失败：" + path);
    }
    void ValidateTargets()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in remote.Keys) paths.Add(path, path);
        foreach (var item in selected.Values)
        {
            if (paths.TryGetValue(item.Entry.Path, out string? previous) && previous != item.Entry.Path) throw new IOException("整合包文件目标仅大小写不同：" + item.Entry.Path);
            paths[item.Entry.Path] = item.Entry.Path;
        }
        foreach (string path in paths.Keys)
        {
            string parent = path;
            while (parent.Contains('/')) { parent = parent[..parent.LastIndexOf('/')]; if (paths.ContainsKey(parent)) throw new IOException("整合包文件与目录目标冲突：" + parent); }
        }
        long total = 0;
        foreach (var node in Index["files"]!.AsArray()) { long length = node!["fileSize"]!.GetValue<long>(); if (length > MaximumSize - total) throw new IOException("整合包安装规模超过 10 GB。"); total += length; }
        foreach (var item in selected.Values) { if (item.Entry.Length > MaximumSize - total) throw new IOException("整合包安装规模超过 10 GB。"); total += item.Entry.Length; entries.Add(item.Entry); }
    }
    public void Dispose() => Archive.Dispose();
}
