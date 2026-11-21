using BlockDock;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

static class PackImportChecks
{
    static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    static void Reject(Action action, string label)
    {
        bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }
    static void Canceled(Action action, string label)
    {
        bool canceled = false; try { action(); } catch (OperationCanceledException) { canceled = true; } Check(canceled, label);
    }
    sealed record Entry(string Path, byte[] Bytes, int? Attributes = null);
    static Entry Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));
    static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static string Archive(string root, string name, params Entry[] entries)
    {
        string path = Path.Combine(root, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path, CompressionLevel.Fastest); if (item.Attributes is int attributes) entry.ExternalAttributes = attributes;
            using var output = entry.Open(); output.Write(item.Bytes);
        }
        return path;
    }
    static string Mrpack(string minecraft = "1.20.1", string? loader = "fabric-loader", string version = "0.15.11")
    {
        var dependencies = new JsonObject { ["minecraft"] = minecraft }; if (loader != null) dependencies[loader] = version;
        return new JsonObject { ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = "fixture-1", ["name"] = "隔离导入测试", ["files"] = new JsonArray(), ["dependencies"] = dependencies }.ToJsonString();
    }
    static void Dependencies(PackArchive pack, string minecraft, string? loader, string? version, string label)
    {
        Check(pack.Index["dependencies"]?["minecraft"]?.ToString() == minecraft && (loader == null ? pack.Index["dependencies"]!.AsObject().Count == 1 : pack.Index["dependencies"]?[loader]?.ToString() == version), label + " resolves exact game and loader versions");
        Check(!string.IsNullOrWhiteSpace(pack.Format) && !string.IsNullOrWhiteSpace(pack.Index["name"]?.ToString()) && pack.Index["files"] is JsonArray, label + " produces named canonical metadata and a file list");
    }
    static void Extract(PackArchive pack, string target, IProgress<WorkProgress>? progress = null, CancellationToken token = default) =>
        ResourceService.CopyPackEntriesAsync(pack, target, progress ?? new InlineProgress<WorkProgress>(_ => { }), token).GetAwaiter().GetResult();
    static string Read(string target, string path) => File.ReadAllText(Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)));
    static void RejectedArchive(string path, string label) => Reject(() => { using var pack = PackArchive.Open(path); }, label);

    public static void Run()
    {
        string root = Path.GetFullPath("work/pack-import-checks/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        MrpackChecks(root); NativeFormats(root); PlainInstances(root); PathChecks(root); SizeChecks(root); CancellationChecks(root); LocalVersionChecks(root); FullImportChecks(root);
        Console.WriteLine("PACK IMPORT CHECKS PASSED; synthetic archives and isolated destinations only");
    }
    static void MrpackChecks(string root)
    {
        // Enumeration order must not decide whether common or client-specific settings win.
        string path = Archive(root, "nested-mrpack.zip", Text("外层/client-overrides/config/shared.txt", "client preference"), Text("外层/modrinth.index.json", Mrpack()), Text("外层/overrides/config/shared.txt", "common preference"), Text("外层/overrides/mods/fixture.jar", "synthetic mod payload"), Text("外层/overrides/options.txt", "lang:zh_cn"), Text("外层/server-overrides/config/server-only.txt", "server only"));
        string before = Hash(path), target = Path.Combine(root, "mrpack-output");
        using (var pack = PackArchive.Open(path))
        {
            Dependencies(pack, "1.20.1", "fabric-loader", "0.15.11", "nested MRPACK with .zip extension");
            Check(pack.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pack.Entries.Count, "client override plan has one final entry per destination"); Extract(pack, target);
        }
        Check(Read(target, "config/shared.txt") == "client preference" && Read(target, "mods/fixture.jar") == "synthetic mod payload" && Read(target, "options.txt") == "lang:zh_cn", "MRPACK extraction persists client overrides, mods and options");
        Check(!File.Exists(Path.Combine(target, "config/server-only.txt")) && !File.Exists(Path.Combine(target, "modrinth.index.json")) && before == Hash(path), "client extraction excludes server-only metadata and preserves the source ZIP");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(stream.Length > 0, "disposing a pack releases its archive handle");
        var remote = JsonNode.Parse(Mrpack())!.AsObject(); remote["files"]!.AsArray().Add(new JsonObject { ["path"] = "mods/remote.jar", ["hashes"] = new JsonObject { ["sha1"] = new string('a', 40) }, ["downloads"] = new JsonArray("https://example.invalid/remote.jar"), ["fileSize"] = 123 });
        string remotePath = Archive(root, "remote-plan.mrpack", Text("modrinth.index.json", remote.ToJsonString()), Text("overrides/config/local.txt", "local")); using var remotePack = PackArchive.Open(remotePath);
        Check(remotePack.Index["files"]!.AsArray().Count == 1, "MRPACK remote file metadata is retained without resolving URLs");
        string remoteTarget = Path.Combine(root, "remote-output"); Extract(remotePack, remoteTarget);
        Check(Read(remoteTarget, "config/local.txt") == "local" && !File.Exists(Path.Combine(remoteTarget, "mods/remote.jar")), "local-entry extraction performs no remote download");
    }
    static void NativeFormats(string root)
    {
        string hmcl = Archive(root, "hmcl.zip", Text("套目录/modpack.json", """{"name":"HMCL 测试","author":"fixture","version":"1","gameVersion":"1.20.1"}"""), Text("套目录/minecraft/pack.json", """{"id":"HMCL fixture","jar":"1.20.1","patches":[{"id":"fabric","version":"0.15.11","libraries":[{"name":"net.fabricmc:fabric-loader:0.15.11"}]}]}"""), Text("套目录/minecraft/mods/hmcl.jar", "hmcl mod"), Text("套目录/minecraft/config/hmcl.cfg", "hmcl config"), Text("套目录/minecraft/options.txt", "hmcl options"));
        using (var pack = PackArchive.Open(hmcl))
        {
            Dependencies(pack, "1.20.1", "fabric-loader", "0.15.11", "HMCL native modpack.json"); string target = Path.Combine(root, "hmcl-output"); Extract(pack, target);
            Check(Read(target, "mods/hmcl.jar") == "hmcl mod" && Read(target, "config/hmcl.cfg") == "hmcl config" && !File.Exists(Path.Combine(target, "pack.json")), "HMCL minecraft contents map to the instance root without copying version metadata");
        }
        foreach (int version in new[] { 1, 2 })
        {
            var meta = new JsonObject { ["manifestType"] = "minecraftModpack", ["manifestVersion"] = version, ["name"] = "MCBBS 测试", ["version"] = "1", ["addons"] = new JsonArray(new JsonObject { ["id"] = "game", ["version"] = "1.20.1" }, new JsonObject { ["id"] = "forge", ["version"] = "47.2.20" }), ["files"] = new JsonArray(new JsonObject { ["type"] = "addon", ["path"] = "mods/mcbbs.jar", ["hash"] = Sha1("mcbbs mod") }) };
            string path = Archive(root, "mcbbs-" + version + ".zip", Text("mcbbs.packmeta", meta.ToJsonString()), Text("overrides/mods/mcbbs.jar", "mcbbs mod"), Text("overrides/config/client.cfg", "mcbbs config"));
            using var pack = PackArchive.Open(path); Dependencies(pack, "1.20.1", "forge", "47.2.20", "MCBBS manifest " + version); string target = Path.Combine(root, "mcbbs-output-" + version); Extract(pack, target);
            Check(Read(target, "mods/mcbbs.jar") == "mcbbs mod" && Read(target, "config/client.cfg") == "mcbbs config", "MCBBS local addons persist after extraction");
        }
        foreach (string gameFolder in new[] { "minecraft", ".minecraft" })
        {
            string suffix = gameFolder.StartsWith('.') ? "dot" : "plain";
            string path = Archive(root, "multimc-" + suffix + ".zip", Text("Wrapped/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.20.4"},{"uid":"net.neoforged","version":"20.4.237"}]}"""), Text("Wrapped/instance.cfg", "[General]\nname=MultiMC 测试\n"), Text("Wrapped/" + gameFolder + "/mods/mmc.jar", "multimc mod"), Text("Wrapped/" + gameFolder + "/options.txt", "multimc options"));
            using var pack = PackArchive.Open(path); Dependencies(pack, "1.20.4", "neoforge", "20.4.237", "MultiMC " + gameFolder); string target = Path.Combine(root, "mmc-output-" + suffix); Extract(pack, target);
            Check(Read(target, "mods/mmc.jar") == "multimc mod" && Read(target, "options.txt") == "multimc options" && !File.Exists(Path.Combine(target, "instance.cfg")), "MultiMC content preserves instance-relative paths");
        }
        string conflicting = Archive(root, "hmcl-version-conflict.zip", Text("modpack.json", """{"name":"Conflict","gameVersion":"1.20.1"}"""), Text("minecraft/pack.json", """{"id":"pack","jar":"1.21.1"}"""), Text("minecraft/options.txt", "x"));
        RejectedArchive(conflicting, "reject HMCL metadata with conflicting game versions");
    }
    static void PlainInstances(string root)
    {
        const string versionJson = """{"id":"Fixture","inheritsFrom":"1.20.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.15.11"}]}""";
        string path = Archive(root, "plain-instance.zip", Text("外层/.minecraft/versions/Fixture/Fixture.json", versionJson), Text("外层/.minecraft/versions/1.20.1/1.20.1.json", """{"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main"}"""), Text("外层/.minecraft/versions/Fixture/mods/local.jar", "local mod"), Text("外层/.minecraft/config/shared.cfg", "shared config"), Text("外层/.minecraft/versions/Fixture/options.txt", "local options"));
        using (var pack = PackArchive.Open(path))
        {
            Dependencies(pack, "1.20.1", "fabric-loader", "0.15.11", "wrapped single-instance PCL folder with a vanilla parent"); string target = Path.Combine(root, "plain-output"); Extract(pack, target);
            Check(Read(target, "mods/local.jar") == "local mod" && Read(target, "config/shared.cfg") == "shared config" && Read(target, "options.txt") == "local options" && !Directory.Exists(Path.Combine(target, "versions")), "single-instance ZIP combines safe shared and isolated content without reinstalling bundled version metadata");
        }
        string collision = Archive(root, "shared-instance-conflict.zip", Text(".minecraft/versions/Fixture/Fixture.json", versionJson), Text(".minecraft/config/shared.cfg", "shared"), Text(".minecraft/versions/Fixture/config/shared.cfg", "isolated"));
        using (var pack = PackArchive.Open(collision))
        {
            string target = Path.Combine(root, "isolated-priority-output"); Extract(pack, target);
            Check(Read(target, "config/shared.cfg") == "isolated" && pack.Entries.Count(e => e.Path == "config/shared.cfg") == 1,
                "the selected isolated instance overrides same-path shared settings with one final entry");
        }
        string caseConflict = Archive(root, "shared-isolated-case-conflict.zip", Text(".minecraft/versions/Fixture/Fixture.json", versionJson), Text(".minecraft/config/Shared.cfg", "shared"), Text(".minecraft/versions/Fixture/config/shared.cfg", "isolated"));
        RejectedArchive(caseConflict, "shared and isolated paths differing only by case remain ambiguous and are rejected");
        string multiple = Archive(root, "plain-multiple.zip", Text(".minecraft/versions/A/A.json", """{"id":"A","inheritsFrom":"1.20.1"}"""), Text(".minecraft/versions/B/B.json", """{"id":"B","inheritsFrom":"1.21.1"}"""), Text(".minecraft/options.txt", "x"));
        RejectedArchive(multiple, "unrelated multiple plain instances are rejected");
    }
    static void PathChecks(string root)
    {
        foreach (string bad in new[] { "../escape.txt", "/absolute.txt", "D:/drive.txt", "mods\\escape.jar", "config/../../escape.txt", "mods/test.jar:payload", "mods/CON.jar", "config/aux/settings.txt", "mods/LPT1.txt", "config/trailing./x.txt", "config/trailing /x.txt", "config//empty.txt", "config/question?.txt", "config/star*.txt" })
        {
            string path = Archive(root, "bad-path-" + Guid.NewGuid().ToString("N") + ".zip", Text("modrinth.index.json", Mrpack()), Text("overrides/" + bad, "must not escape")); RejectedArchive(path, "reject unsafe archive output path: " + bad);
        }
        string symlink = Archive(root, "symlink.zip", Text("modrinth.index.json", Mrpack()), new("overrides/config/link", Encoding.UTF8.GetBytes("../../outside"), unchecked((int)0xA1FF0000)));
        RejectedArchive(symlink, "reject Unix symbolic-link ZIP entries");
        foreach (var entries in new[] { new[] { Text("overrides/config/a.txt", "one"), Text("overrides/config/a.txt", "two") }, new[] { Text("overrides/config/A.txt", "one"), Text("overrides/config/a.txt", "two") }, new[] { Text("overrides/config", "file"), Text("overrides/config/a.txt", "child") } })
        {
            string path = Archive(root, "conflict-" + Guid.NewGuid().ToString("N") + ".zip", new[] { Text("modrinth.index.json", Mrpack()) }.Concat(entries).ToArray()); RejectedArchive(path, "reject duplicate, case-insensitive or file-directory output collision");
        }
        string multiple = Archive(root, "multiple-instances.zip", Text("A/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.20.1"}]}"""), Text("A/minecraft/options.txt", "a"), Text("B/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.21.1"}]}"""), Text("B/minecraft/options.txt", "b"));
        RejectedArchive(multiple, "multiple instance manifests are not chosen silently");
        var metadata = JsonNode.Parse(Mrpack())!.AsObject(); metadata["dependencies"]!["forge"] = "47.2.20";
        RejectedArchive(Archive(root, "multiple-loaders.zip", Text("modrinth.index.json", metadata.ToJsonString())), "conflicting Fabric and Forge requirements are rejected");
    }
    static void SizeChecks(string root)
    {
        RejectedArchive(Archive(root, "large-manifest.zip", Text("modrinth.index.json", Mrpack() + new string(' ', 11 * 1024 * 1024))), "reject manifests exceeding the bounded JSON size");
        string huge = Archive(root, "claimed-expansion.zip", Text("modrinth.index.json", Mrpack()), Text("overrides/mods/a.jar", "small"), Text("overrides/mods/b.jar", "small"), Text("overrides/mods/c.jar", "small")); PatchClaimedSizes(huge, 4_000_000_000u);
        using (var zip = ZipFile.OpenRead(huge)) Check(zip.Entries.Where(e => e.FullName.StartsWith("overrides/")).Sum(e => e.Length) == 12_000_000_000L, "size-limit fixture claims 12 GB without allocating or extracting it");
        RejectedArchive(huge, "reject ZIP expansion above 10 GiB before extraction");
    }
    static void PatchClaimedSizes(string path, uint size)
    {
        byte[] data = File.ReadAllBytes(path);
        for (int n = 0; n <= data.Length - 46; n++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(n, 4)) != 0x02014b50) continue;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(n + 28, 2)); if (n + 46 + length > data.Length) throw new InvalidDataException("bad fixture central directory");
            string name = Encoding.UTF8.GetString(data, n + 46, length); if (!name.StartsWith("overrides/")) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(n + 24, 4), size); int local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(n + 42, 4))); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(local + 22, 4), size);
        }
        File.WriteAllBytes(path, data);
    }
    static void CancellationChecks(string root)
    {
        string path = Archive(root, "cancel.zip", Text("modrinth.index.json", Mrpack()), new("overrides/config/shared.txt", Enumerable.Repeat((byte)'x', 4 * 1024 * 1024).ToArray())); string sourceHash = Hash(path);
        using (var pre = new CancellationTokenSource())
        {
            pre.Cancel(); Canceled(() => { using var ignored = PackArchive.Open(path, pre.Token); }, "opening an archive honors a pre-canceled token"); using var plan = PackArchive.Open(path); string target = Path.Combine(root, "never-created");
            Canceled(() => Extract(plan, target, token: pre.Token), "pre-canceled extraction stops before touching the target"); Check(!Directory.Exists(target), "pre-canceled extraction leaves no target directory");
        }
        using var pack = PackArchive.Open(path); string destination = Path.Combine(root, "cancel-output"); Directory.CreateDirectory(Path.Combine(destination, "config")); File.WriteAllText(Path.Combine(destination, "config/shared.txt"), "existing user configuration"); File.WriteAllText(Path.Combine(destination, "keep.txt"), "unrelated existing file");
        using var cancel = new CancellationTokenSource(); bool copying = false; var progress = new InlineProgress<WorkProgress>(p => { if (p.Bytes > 0) { copying = true; cancel.Cancel(); } });
        Canceled(() => Extract(pack, destination, progress, cancel.Token), "canceling mid-copy aborts before committing a replacement");
        Check(copying && Read(destination, "config/shared.txt") == "existing user configuration" && Read(destination, "keep.txt") == "unrelated existing file", "mid-copy cancellation preserves existing destination and unrelated files");
        Check(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count() == 2 && Hash(path) == sourceHash, "cancellation removes only its temporary file and preserves the source ZIP");
    }

    static void LocalVersionChecks(string root)
    {
        var settings = new Settings { JavaRoot = Path.Combine(root, "local-version-checks") }; InstalledVanilla(settings);
        var vanilla = JsonNode.Parse(Mrpack(loader: null))!.AsObject();
        var fabric = JsonNode.Parse(Mrpack(version: "0.16.10"))!.AsObject();
        Check(PackLocalVersions.Find(settings, vanilla) == "1.20.1", "local matcher accepts a valid installed vanilla engine");
        Check(PackLocalVersions.Find(settings, JsonNode.Parse(Mrpack("1.21.1", null))!.AsObject()) == null,
            "local matcher does not substitute a different Minecraft version");
        string folder = Path.Combine(settings.JavaRoot, "versions", "Fabric-fixture"), file = Path.Combine(folder, "Fabric-fixture.json"); Directory.CreateDirectory(folder);
        const string json = """{"id":"Fabric-fixture","inheritsFrom":"1.20.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}]}""";
        File.WriteAllText(file, json);
        Check(PackLocalVersions.Find(settings, fabric) == "Fabric-fixture", "local matcher accepts the exact Fabric loader and vanilla parent");
        Check(PackLocalVersions.Find(settings, JsonNode.Parse(Mrpack(version: "0.16.11"))!.AsObject()) == null,
            "local matcher rejects a different loader version");
        var invalid = JsonNode.Parse(json)!.AsObject(); invalid["minecraftVersion"] = "1.21.1"; File.WriteAllText(file, invalid.ToJsonString());
        Check(PackLocalVersions.Find(settings, fabric) == null, "conflicting local Minecraft metadata cannot establish a compatible engine");
        invalid = JsonNode.Parse(json)!.AsObject(); invalid["id"] = "Different-id"; File.WriteAllText(file, invalid.ToJsonString());
        Check(PackLocalVersions.Find(settings, fabric) == null, "local JSON id must match its actual version directory");
        invalid = JsonNode.Parse(json)!.AsObject(); invalid["libraries"] = new JsonArray(); File.WriteAllText(file, invalid.ToJsonString());
        File.WriteAllText(Path.Combine(folder, ".blockdock-profile.json"), """{"minecraft":"1.20.1","loader":"fabric"}""");
        Check(PackLocalVersions.Find(settings, fabric) == null, "a profile label cannot replace missing loader metadata");
        File.WriteAllText(file, json);
        string childPending = Path.Combine(folder, ".blockdock-pending"); File.WriteAllText(childPending, "incomplete");
        Check(PackLocalVersions.Find(settings, fabric) == null, "unfinished loader profiles are not reused"); File.Delete(childPending);
        string baseFolder = Path.Combine(settings.JavaRoot, "versions", "1.20.1"), parentPending = Path.Combine(baseFolder, ".blockdock-pending"); File.WriteAllText(parentPending, "incomplete");
        Check(PackLocalVersions.Find(settings, fabric) == null && PackLocalVersions.Find(settings, vanilla) == null, "unfinished parent engines invalidate both vanilla and loader matches"); File.Delete(parentPending);
        string jar = Path.Combine(baseFolder, "1.20.1.jar"); byte[] bytes = File.ReadAllBytes(jar); File.Delete(jar);
        Check(PackLocalVersions.Find(settings, fabric) == null && PackLocalVersions.Find(settings, vanilla) == null, "missing client JAR prevents local-engine reuse");
        File.WriteAllBytes(jar, Array.Empty<byte>()); Check(PackLocalVersions.Find(settings, fabric) == null, "an empty client JAR cannot satisfy a local engine"); File.WriteAllBytes(jar, bytes);
        Check(PackLocalVersions.Find(settings, fabric) == "Fabric-fixture", "restoring complete matching metadata makes the local engine usable again");
    }

    static void FullImportChecks(string root)
    {
        var settings = new Settings { JavaRoot = Path.Combine(root, "full-java-a"), Source = "Unconfigured" };
        var secondSettings = new Settings { JavaRoot = Path.Combine(root, "full-java-b"), Source = "Unconfigured" };
        var ownedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string OwnJob(string package, string name, Settings destination)
        {
            string text = Hash(package) + "\n" + name + "\n" + Path.GetFullPath(destination.JavaRoot).TrimEnd('\\', '/').ToUpperInvariant();
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            string path = Path.Combine(Settings.DataPath, "pack-jobs", key + ".json");
            ownedJobs.Add(path);
            return path;
        }
        GameInstance Import(ResourceService service, Settings destination, string package, string name, IProgress<WorkProgress>? progress = null, CancellationToken token = default)
        {
            OwnJob(package, name, destination);
            return service.ImportPackAsync(package, name, progress ?? new InlineProgress<WorkProgress>(_ => { }), token).GetAwaiter().GetResult();
        }
        try
        {
            InstalledVanilla(settings); InstalledVanilla(secondSettings);
            var index = JsonNode.Parse(Mrpack(loader: null))!.AsObject();
            Check(PackLocalVersions.Find(settings, index) == "1.20.1" && PackLocalVersions.Find(secondSettings, index) == "1.20.1",
                "full-import fixtures have matching local game engines before any import can attempt a download");
            string vanillaJson = Path.Combine(settings.JavaRoot, "versions/1.20.1/1.20.1.json"), vanillaJar = Path.Combine(settings.JavaRoot, "versions/1.20.1/1.20.1.jar");
            string sourceJsonHash = Hash(vanillaJson), sourceJarHash = Hash(vanillaJar);
            string package = Archive(root, "full-workflow.zip", Text("modrinth.index.json", index.ToJsonString()), Text("overrides/mods/full.jar", "complete mod fixture"), Text("overrides/config/settings.txt", "pack defaults"));
            using var launch = new LaunchCore(settings);
            var occupied = launch.CreateInstance("1.20.1", "1.20.1", "vanilla", "完整导入测试");
            File.WriteAllText(Path.Combine(occupied.GameDirectory, "keep.txt"), "existing named instance");
            using var service = new ResourceService(settings); using var secondService = new ResourceService(secondSettings);
            var game = Import(service, settings, package, "完整导入测试");
            var scanned = LaunchCore.Scan(settings).Single(g => g.Id == game.Id);
            Check(!game.Pending && !scanned.Pending && game.Id != occupied.Id && scanned.GameDirectory == game.GameDirectory && game.GameDirectory == game.Folder && game.Folder != settings.JavaRoot,
                "complete offline import creates a separately scanned instance and preserves an existing instance name");
            Check(Read(game.GameDirectory, "mods/full.jar") == "complete mod fixture" && Read(game.GameDirectory, "config/settings.txt") == "pack defaults" && Read(occupied.GameDirectory, "keep.txt") == "existing named instance",
                "complete import persists mods and config without overwriting the pre-existing named instance");
            Check(Hash(vanillaJson) == sourceJsonHash && Hash(vanillaJar) == sourceJarHash, "importing from a local engine leaves its original version JSON and client JAR unchanged");
            File.WriteAllText(Path.Combine(game.GameDirectory, "config/settings.txt"), "user edited after import");
            var repeated = Import(service, settings, package, "完整导入测试");
            Check(repeated.Id == game.Id && Read(game.GameDirectory, "config/settings.txt") == "user edited after import", "same package hash and name reuse the completed instance without resetting user edits");

            using var secondLaunch = new LaunchCore(secondSettings);
            var unrelated = secondLaunch.CreateInstance("1.20.1", "1.20.1", "vanilla", game.Id);
            File.WriteAllText(Path.Combine(unrelated.GameDirectory, "keep.txt"), "different game root content");
            var elsewhere = Import(secondService, secondSettings, package, "完整导入测试");
            Check(elsewhere.Id != unrelated.Id && elsewhere.Folder.StartsWith(Path.GetFullPath(secondSettings.JavaRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Read(elsewhere.GameDirectory, "mods/full.jar") == "complete mod fixture" && Read(unrelated.GameDirectory, "keep.txt") == "different game root content",
                "changing JavaRoot cannot reuse a completed job belonging to another root with a matching instance id");
            string different = Archive(root, "full-other-hash.zip", Text("modrinth.index.json", index.ToJsonString()), Text("overrides/config/settings.txt", "different package content"));
            var distinct = Import(service, settings, different, "完整导入测试");
            Check(distinct.Id != game.Id && distinct.Id != occupied.Id && Read(distinct.GameDirectory, "config/settings.txt") == "different package content" && Read(game.GameDirectory, "config/settings.txt") == "user edited after import",
                "different package content with the same display name creates a new instance");

            foreach (bool complete in new[] { true, false })
            foreach (string receiptCase in new[] { "missing", "wrong-hash", "wrong-name" })
            {
                string label = (complete ? "complete" : "pending") + "-" + receiptCase;
                string staleName = "旧记录归属测试-" + label;
                string stalePackage = Archive(root, "full-stale-" + label + ".zip", Text("modrinth.index.json", index.ToJsonString()), Text("overrides/config/settings.txt", "original pack defaults " + label));
                var original = Import(service, settings, stalePackage, staleName);
                string receiptPath = Path.Combine(original.Folder, ".blockdock-pack.json");
                if (receiptCase == "missing") File.Delete(receiptPath);
                else
                {
                    var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
                    receipt[receiptCase == "wrong-hash" ? "sha256" : "name"] = receiptCase == "wrong-hash" ? new string('f', 64) : "unrelated recreated instance";
                    File.WriteAllText(receiptPath, receipt.ToJsonString());
                }
                File.WriteAllText(Path.Combine(original.GameDirectory, "config/settings.txt"), "recreated instance user config " + label);
                string jobPath = OwnJob(stalePackage, staleName, settings);
                var staleState = JsonNode.Parse(File.ReadAllText(jobPath))!.AsObject();
                staleState["complete"] = complete;
                File.WriteAllText(jobPath, staleState.ToJsonString());
                var originalFiles = Directory.GetFiles(original.Folder, "*", SearchOption.AllDirectories)
                    .ToDictionary(file => Path.GetRelativePath(original.Folder, file), Hash, StringComparer.OrdinalIgnoreCase);

                var replacement = Import(service, settings, stalePackage, staleName);
                Check(replacement.Id != original.Id && Read(replacement.GameDirectory, "config/settings.txt") == "original pack defaults " + label,
                    "stale " + label + " job creates a separate instance instead of reusing an unowned id");
                var retainedFiles = Directory.GetFiles(original.Folder, "*", SearchOption.AllDirectories)
                    .ToDictionary(file => Path.GetRelativePath(original.Folder, file), Hash, StringComparer.OrdinalIgnoreCase);
                Check(retainedFiles.Count == originalFiles.Count && originalFiles.All(file => retainedFiles.TryGetValue(file.Key, out string? hash) && hash == file.Value),
                    "stale " + label + " job leaves every unrelated instance file and user setting unchanged");
            }

            string retryPack = Archive(root, "full-cancel-retry.zip", Text("modrinth.index.json", index.ToJsonString()), new("overrides/config/retry.bin", Enumerable.Repeat((byte)'r', 4 * 1024 * 1024).ToArray()));
            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress<WorkProgress>(p => { if (p.Bytes > 0) cancel.Cancel(); });
            Canceled(() => Import(service, settings, retryPack, "可重试测试", progress, cancel.Token), "complete import can be canceled during content extraction");
            var pending = LaunchCore.Scan(settings).Single(g => g.Name == "可重试测试");
            Check(pending.Pending && File.Exists(Path.Combine(pending.Folder, ".blockdock-pending")) && !File.Exists(Path.Combine(pending.GameDirectory, "config/retry.bin")),
                "canceled import remains explicitly pending without publishing a partial content file");
            var resumed = Import(service, settings, retryPack, "可重试测试");
            Check(resumed.Id == pending.Id && !LaunchCore.Scan(settings).Single(g => g.Id == resumed.Id).Pending && new FileInfo(Path.Combine(resumed.GameDirectory, "config/retry.bin")).Length == 4 * 1024 * 1024 && LaunchCore.Scan(settings).Count(g => g.Name == "可重试测试") == 1,
                "retry finishes the same pending instance without duplicating or leaving an unfinished marker");
            int count = LaunchCore.Scan(settings).Count;
            Reject(() => service.ImportPackAsync(package, "已变化测试", new InlineProgress<WorkProgress>(_ => { }), default, new string('0', 64)).GetAwaiter().GetResult(),
                "import rejects an archive whose hash differs from the preview");
            Check(LaunchCore.Scan(settings).Count == count, "preview hash mismatch creates no instance");
        }
        finally
        {
            // Remove only metadata keys belonging to this run's unique roots, never other import jobs.
            foreach (string file in ownedJobs) if (File.Exists(file)) File.Delete(file);
        }
    }
    static void InstalledVanilla(Settings settings)
    {
        string folder = Path.Combine(settings.JavaRoot, "versions", "1.20.1"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1.20.1.json"), """{"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        File.WriteAllBytes(Path.Combine(folder, "1.20.1.jar"), Encoding.ASCII.GetBytes("synthetic non-executable client fixture"));
    }
}
