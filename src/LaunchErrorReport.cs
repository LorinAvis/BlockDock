using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockDock;

public sealed record LaunchErrorReport(string Summary, string Details)
{
    public string Text => Summary + Environment.NewLine + Environment.NewLine + Details;
    public static string LastFile => Path.Combine(Settings.DataPath, "reports", "last-launch.txt");

    public void Save(string file) => Settings.AtomicText(file, Text);

    public static LaunchErrorReport? ReadLast()
    {
        try
        {
            WorldFiles.NoLinks(LastFile);
            using var file = File.OpenRead(LastFile);
            if (file.Length is 0 or > 512 * 1024) return null;
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            var parts = Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n").Split("\n\n", 2);
            if (parts.Length == 2) return new(parts[0].ReplaceLineEndings(), parts[1].ReplaceLineEndings());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}

// One object per launch. Download jobs and other instances never write to these buffers.
public sealed class LaunchDiagnostics
{
    const int LineLimit = 1100;
    readonly string edition, version;
    readonly string? gameDirectory;
    readonly string[] identities;
    readonly Queue<string> output = new(), important = new();
    readonly object gate = new();
    readonly (long Length, byte[] Tail) previousLatest;
    public string Name { get; }
    public string LastStep { get; set; } = "";

    public LaunchDiagnostics(string name, string edition, string version, string? gameDirectory = null, params string[] identities)
    {
        Name = name; this.edition = edition; this.version = version;
        this.gameDirectory = gameDirectory;
        this.identities = identities.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length).ToArray();
        if (gameDirectory != null)
        {
            try
            {
                using var file = OpenLog(Path.Combine(gameDirectory, "logs", "latest.log"));
                long length = file.Length;
                var tail = ReadRange(file, Math.Max(0, length - 128), length);
                previousLatest = (length, tail);
            }
            catch (Exception e) when (Unreadable(e)) { }
        }
    }

    public void RecordOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || IsPrivateLine(line)) return;
        string value = Clip(MessageText(line), LineLimit * 2);
        if (value.Length == 0) return;
        lock (gate)
        {
            output.Enqueue(value);
            if (output.Count > 160) output.Dequeue();
            if (Score(value) > 0)
            {
                important.Enqueue(value);
                if (important.Count > 32) important.Dequeue();
            }
        }
    }

    public LaunchErrorReport Build(string stage, DateTimeOffset? processStarted = null, int? exitCode = null, Exception? error = null)
    {
        var sections = new List<(string Title, List<string> Lines)>();
        if (error != null)
        {
            var errors = new List<string>();
            for (var cause = error; cause != null && errors.Count < 8; cause = cause.InnerException)
            {
                var message = Clean(cause.Message.Split('\n', 2)[0]);
                errors.Add(cause.GetType().Name + (string.IsNullOrWhiteSpace(message) ? "" : " · " + message));
            }
            sections.Add(("启动器异常", errors));
        }

        if (processStarted.HasValue && gameDirectory != null)
        {
            var files = ReadGameLogs(processStarted.Value);
            foreach (var (title, lines) in files) Add(title, lines);
        }
        string[] consoleLines;
        lock (gate) consoleLines = important.Concat(output).ToArray();
        Add("游戏进程输出", consoleLines);

        // Prefer the deepest cause at the same severity, with the crash report first.
        string? key = error != null ? sections[0].Lines[^1] : sections.SelectMany(s => s.Lines.AsEnumerable().Reverse())
            .Where(line => Score(line) > 0).OrderByDescending(Score).FirstOrDefault();
        string reason = Explain(key);
        string SafeLabel(string value) => Clean(value.ReplaceLineEndings(" ")) ?? "（已隐藏）";
        var summary = new StringBuilder("BlockDock 游戏错误报告").AppendLine()
            .AppendLine(reason)
            .AppendLine("实例　" + SafeLabel(Name))
            .AppendLine("游戏　" + SafeLabel(edition + " " + version))
            .AppendLine("阶段　" + SafeLabel(stage) + (exitCode is int code ? "　退出码 " + code : ""))
            .AppendLine("时间　" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "　BlockDock " + LauncherVersion.Number);
        if (key != null && key != reason) summary.AppendLine("关键报错　" + Clip(key, 520));
        var details = new StringBuilder();
        string? step = Clean(LastStep);
        if (!processStarted.HasValue && !string.IsNullOrWhiteSpace(step)) details.AppendLine("最后步骤　" + step).AppendLine();
        foreach (var section in sections)
        {
            details.AppendLine("【" + section.Title + "】");
            foreach (var line in section.Lines) details.AppendLine(line);
            details.AppendLine();
        }
        if (sections.Count == 0) details.AppendLine("本次启动没有留下可用的异常日志。");
        return new(summary.ToString().TrimEnd(), details.ToString().TrimEnd());

        void Add(string title, IEnumerable<string> lines)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int frames = 0;
            foreach (string raw in lines)
            {
                string text = MessageText(raw);
                if (Score(text) > 0) frames = 5;
                else if (frames > 0 && text.StartsWith("at ", StringComparison.Ordinal)) frames--;
                else if (title != "Minecraft 崩溃报告" || !ModField.IsMatch(text)) continue;
                string? safe = Clean(text);
                if (string.IsNullOrWhiteSpace(safe) || !seen.Add(safe)) continue;
                result.Add(safe);
            }
            // Keep useful root causes even when the game writes hundreds of stack frames.
            if (result.Count > 40)
            {
                var keep = result.Select((line, i) => (line, i)).OrderByDescending(x => Score(x.line)).ThenByDescending(x => x.i).Take(40).OrderBy(x => x.i);
                result = keep.Select(x => x.line).ToList();
            }
            if (result.Count > 0) sections.Add((title, result));
        }
    }

    List<(string Title, string[] Lines)> ReadGameLogs(DateTimeOffset start)
    {
        var files = new List<(string, string[])>();
        DateTime now = DateTime.UtcNow;
        try
        {
            string folder = Path.Combine(gameDirectory!, "crash-reports");
            WorldFiles.NoLinks(folder);
            if (Directory.Exists(folder))
            {
                var crash = new DirectoryInfo(folder).EnumerateFiles("crash-*.txt")
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTimeUtc >= start.UtcDateTime && f.LastWriteTimeUtc <= now)
                    .MaxBy(f => f.LastWriteTimeUtc);
                if (crash != null)
                {
                    using var file = OpenLog(crash.FullName);
                    string header = Encoding.UTF8.GetString(ReadRange(file, 0, Math.Min(file.Length, 192 * 1024)));
                    files.Add(("Minecraft 崩溃报告", header.Split("-- System Details --", 2)[0].Split('\n')));
                }
            }
        }
        catch (Exception e) when (Unreadable(e)) { }

        try
        {
            string path = Path.Combine(gameDirectory!, "logs", "latest.log");
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (modified < start.UtcDateTime || modified > now) return files;
            using var file = OpenLog(path);
            long end = file.Length, begin = 0;
            var (oldLength, oldTail) = previousLatest;
            if (oldTail is { Length: > 0 } && oldLength <= end &&
                ReadRange(file, oldLength - oldTail.Length, oldLength).AsSpan().SequenceEqual(oldTail)) begin = oldLength;
            // Appended logs start at the old end. Replaced logs start at zero.
            long offset = Math.Max(begin, end - 192 * 1024);
            string text = Encoding.UTF8.GetString(ReadRange(file, offset, end));
            if (offset > begin) { int newline = text.IndexOf('\n'); text = newline < 0 ? "" : text[(newline + 1)..]; }
            files.Add(("本次 latest.log", text.Split('\n')));
        }
        catch (Exception e) when (Unreadable(e)) { }
        return files;
    }

    static FileStream OpenLog(string path)
    {
        WorldFiles.NoLinks(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    static byte[] ReadRange(FileStream file, long start, long end)
    {
        file.Position = start;
        var bytes = new byte[checked((int)(end - start))];
        file.ReadExactly(bytes);
        return bytes;
    }

    static bool Unreadable(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    static string MessageText(string line)
    {
        int cdata = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
        string message = cdata >= 0 ? line[(cdata + 9)..] : line;
        int end = message.IndexOf("]]>", StringComparison.Ordinal);
        return (end >= 0 ? message[..end] : message).Trim();
    }

    static int Score(string text)
    {
        if (text.StartsWith("at ", StringComparison.Ordinal) || text.StartsWith('<')) return 0;
        if (text.Contains("Caused by:", StringComparison.OrdinalIgnoreCase)) return 6;
        if (RoutineLogLevel.IsMatch(text)) return 0;
        if (text.Contains("ResolutionException", StringComparison.Ordinal) || text.Contains("OutOfMemoryError", StringComparison.Ordinal) ||
            text.Contains("UnsupportedClassVersionError", StringComparison.Ordinal) || text.Contains("Failure message:", StringComparison.OrdinalIgnoreCase)) return 5;
        if (text.Contains("Exception", StringComparison.Ordinal) || text.Contains("Error:", StringComparison.Ordinal) ||
            text.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.Ordinal)) return 4;
        if (text.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || text.Contains("Incompatible mod", StringComparison.OrdinalIgnoreCase)) return 3;
        if (text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || text.Contains("Failed to ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("requires ", StringComparison.OrdinalIgnoreCase) || text.Contains("Description:", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    static readonly Regex RoutineLogLevel = new(@"\[(?:[^\]\r\n]*/)?(?:INFO|DEBUG|TRACE|WARN)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ModField = new(@"^(?:Mod (?:File|ID|Version)|Suspected Mods?)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string Explain(string? key)
    {
        if (key == null) return "进程已退出，现有日志无法确定原因。";
        if (key.Contains("export package", StringComparison.Ordinal) && key.Contains("Modules ", StringComparison.Ordinal))
            return "Java 加载模块时发现两个模块提供了同一个包。";
        if (key.Contains("OutOfMemoryError", StringComparison.Ordinal) && key.Contains("Java heap space", StringComparison.OrdinalIgnoreCase))
            return "Java 的游戏内存已用尽。可在启动设置中检查内存分配。";
        if (key.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
            return "当前 Java 版本无法加载游戏或模组使用的类文件。";
        return Clip(key, 520);
    }

    static readonly Regex PrivateFields = new(
        @"(?ix)(?:--(?:username|uuid|xuid|clientid|userinfo|userproperties|gameDir)\b|(?:access|refresh|client|id|device|session)[_-]?token\b|bearer\s|xbl3\.0\s|(?:authorization|token|xuid|username|session|uuid)\s*[""']?\s*[:=]|(?:password|passwd|cookie|api[_-]?key|client[_-]?secret|identitytoken|rpsticket|private[_-]?key|sessionid|user[_-]?code|device[_-]?code)\b|(?:launch(?:er)?\s+arguments|running:\s*args|command\s*line|jvm\s+(?:args|flags))|\[chat\])",
        RegexOptions.CultureInvariant);
    static bool IsPrivateLine(string text) => PrivateFields.IsMatch(text);
    static readonly Regex HomePath = new(@"(?i)\b[A-Z]:[\\/]+Users[\\/]+[^\\/\r\n:]+", RegexOptions.CultureInvariant);
    static readonly Regex Identifiers = new(@"(?i)\b(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\b|\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b|https?://[^\s<>]+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant);

    string? Clean(string raw)
    {
        if (IsPrivateLine(raw)) return null;
        string text = SkinAccounts.Redact(raw.Trim());
        if (gameDirectory != null)
        {
            text = text.Replace(gameDirectory, "<实例目录>", StringComparison.OrdinalIgnoreCase)
                .Replace(gameDirectory.Replace('\\', '/'), "<实例目录>", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var id in identities) text = text.Replace(id, "[已隐藏]", StringComparison.OrdinalIgnoreCase);
        return Clip(Identifiers.Replace(HomePath.Replace(text, "<用户目录>"), "[已隐藏]"), LineLimit);
    }
}
