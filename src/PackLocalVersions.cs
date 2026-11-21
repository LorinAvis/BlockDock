using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockDock;

// Examines existing engine metadata only. It never launches Java or consults a pack's version JSON.
public static class PackLocalVersions
{
    const long MaximumMetadataBytes = 8 * 1024 * 1024;
    sealed record Request(string Minecraft, string Loader, string LoaderVersion);
    sealed record LocalVersion(string Id, string Folder, JsonObject Data);

    public static string? Find(Settings settings, JsonObject index)
    {
        try
        {
            var request = ReadRequest(index); if (request == null) return null;
            string root = Path.GetFullPath(settings.JavaRoot), versions = Paths.Safe(root, "versions");
            WorldFiles.NoLinks(root); WorldFiles.NoLinks(versions);
            var matches = new List<(string Id, int Depth)>();
            foreach (string folder in WorldFiles.Directories(versions))
            {
                try
                {
                    string id = Paths.Id(Path.GetFileName(folder));
                    var chain = ReadChain(root, id);
                    if (Matches(root, request, chain)) matches.Add((id, chain.Count));
                }
                catch (Exception e) when (Unreadable(e)) { }
            }
            // Prefer the installed engine over an additional user-created inheritance wrapper.
            return matches.OrderBy(m => m.Depth).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(m => m.Id).FirstOrDefault();
        }
        catch (Exception e) when (Unreadable(e)) { return null; }
    }

    static Request? ReadRequest(JsonObject index)
    {
        if (index["dependencies"] is not JsonObject dependencies || Text(dependencies["minecraft"]) is not string minecraft || string.IsNullOrWhiteSpace(minecraft)) return null;
        Paths.Id(minecraft); string loader = "vanilla", version = "";
        foreach (var dependency in dependencies.Where(p => p.Key != "minecraft"))
        {
            if (loader != "vanilla" || Text(dependency.Value) is not string value || string.IsNullOrWhiteSpace(value)) return null;
            loader = dependency.Key switch { "fabric-loader" => "fabric", "forge" => "forge", "neoforge" => "neoforge", _ => "unsupported" };
            if (loader == "unsupported") return null;
            Paths.Id(value); version = value;
        }
        return new(minecraft, loader, version);
    }

    static List<LocalVersion> ReadChain(string root, string source)
    {
        var result = new List<LocalVersion>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string id = source;
        while (true)
        {
            Paths.Id(id); if (!seen.Add(id) || seen.Count > 16) throw new InvalidDataException("本机版本继承链无效。");
            string folder = Paths.Safe(root, "versions/" + id), file = Paths.Safe(folder, id + ".json");
            WorldFiles.NoLinks(folder); WorldFiles.NoLinks(file);
            if (Pending(folder)) throw new InvalidDataException("本机版本尚未完成安装。");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumMetadataBytes) throw new InvalidDataException("本机版本元数据大小无效。");
            var data = JsonNode.Parse(input, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("本机版本元数据无效。");
            if (!string.Equals(Text(data["id"]), id, StringComparison.Ordinal)) throw new InvalidDataException("本机版本编号与目录不一致。");
            result.Add(new(id, folder, data));
            if (data["inheritsFrom"] == null) return result;
            id = Text(data["inheritsFrom"]) ?? throw new InvalidDataException("本机父版本编号无效。");
        }
    }

    static bool Matches(string root, Request request, IReadOnlyList<LocalVersion> chain)
    {
        var terminal = chain[^1];
        // CreateInstance flattens loader layers onto this terminal vanilla parent and its client JAR.
        // A display name, .blockdock-profile claim, or a missing parent never establishes MC identity.
        if (!string.Equals(terminal.Id, request.Minecraft, StringComparison.Ordinal)) return false;
        string? mainClass = null;
        var engines = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in chain)
        {
            foreach (string field in new[] { "clientVersion", "minecraftVersion", "jar" })
                if (node.Data[field] != null && !string.Equals(Text(node.Data[field]), request.Minecraft, StringComparison.Ordinal)) return false;
            if (mainClass == null && node.Data["mainClass"] != null) mainClass = Text(node.Data["mainClass"]) ?? "";
            if (node.Data["libraries"] == null) continue;
            if (node.Data["libraries"] is not JsonArray libraries) return false;
            foreach (var library in libraries)
            {
                if (library is not JsonObject entry || Text(entry["name"]) is not string name || string.IsNullOrWhiteSpace(name)) return false;
                var coordinate = name.Split('@'); if (coordinate.Length > 2) return false;
                var parts = coordinate[0].Split(':'); if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace)) return false;
                string key = parts[0] + ":" + parts[1]; string? family = Family(key); if (family == null) continue;
                // Conditional loader coordinates do not prove the client actually uses that engine.
                if (entry["rules"] != null && entry["rules"] is not JsonArray { Count: 0 }) return false;
                if (coordinate.Length == 2 && coordinate[1] != "jar") return false;
                if (parts.Length == 4 && parts[3] is not ("client" or "universal")) return false;
                if (!engines.TryGetValue(family, out var values)) engines[family] = values = new(StringComparer.Ordinal);
                string value = parts[2];
                if (family == "forge" || family == "neoforge" && parts[1] == "forge")
                {
                    string? version = ForgeVersion(value, request.Minecraft); if (version == null) return false; value = version;
                }
                values.Add(value);
            }
        }
        if (string.IsNullOrWhiteSpace(mainClass)) return false;
        if (request.Loader == "vanilla")
        {
            if (engines.Count != 0 || !PlainClient(mainClass)) return false;
        }
        else if (PlainClient(mainClass) || engines.Count != 1 || !engines.TryGetValue(request.Loader, out var versions)
            || versions.Count != 1 || !versions.Contains(request.LoaderVersion)) return false;

        string jar = Paths.Safe(root, "versions/" + request.Minecraft + "/" + request.Minecraft + ".jar"); WorldFiles.NoLinks(jar);
        using var client = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.Read);
        return client.Length > 0 && chain.All(node => !Pending(node.Folder));
    }

    static string? ForgeVersion(string artifactVersion, string minecraft)
    {
        string prefix = minecraft + "-"; if (!artifactVersion.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string version = artifactVersion[prefix.Length..];
        string suffix = "-" + minecraft;
        if (version.EndsWith(suffix, StringComparison.Ordinal)) version = version[..^suffix.Length];
        else if (Version.TryParse(minecraft, out var parsed) && parsed.Build < 0 && version.EndsWith(suffix + ".0", StringComparison.Ordinal)) version = version[..^(suffix.Length + 2)];
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
    static string? Family(string coordinate) => coordinate switch
    {
        "net.fabricmc:fabric-loader" => "fabric",
        "net.minecraftforge:forge" => "forge",
        "net.neoforged:neoforge" or "net.neoforged:forge" => "neoforge",
        "org.quiltmc:quilt-loader" => "quilt",
        "net.optifine:OptiFine" or "optifine:OptiFine" => "optifine",
        "com.mumfrey:liteloader" => "liteloader",
        _ => null
    };
    static bool PlainClient(string mainClass) => mainClass is "net.minecraft.client.main.Main" or "net.minecraft.client.Minecraft" or "com.mojang.minecraft.Minecraft";
    static bool Pending(string folder)
    {
        string marker = Path.Combine(folder, ".blockdock-pending");
        return File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget != null;
    }
    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
    static bool Unreadable(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException or OverflowException;
}
