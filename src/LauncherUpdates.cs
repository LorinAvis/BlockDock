using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlockDock;

public static class LauncherVersion
{
    public static string Number => typeof(LauncherVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public const bool IsPreview = true;
    public static string Label => Number + (IsPreview ? " 预览版" : "");
}

public sealed record LauncherRelease(string Tag, string Name, string PageUrl, bool Preview);
public sealed record UpdateCheck(LauncherRelease? Available, DateTimeOffset? CheckedAt, bool Cached, string? Error = null);

// Compare version numbers and prerelease identifiers, never release dates or text ordering.
public sealed record ReleaseVersion(Version Number, string[] Prerelease) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string tag, bool preview = false)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag, @"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var values = new int[4];
        for (int i = 0; i < 4; i++)
            if (match.Groups[i + 1].Success && !int.TryParse(match.Groups[i + 1].Value, out values[i])) return null;
        string[] suffix = match.Groups[5].Success ? match.Groups[5].Value.Split('.') : preview ? new[] { "preview" } : Array.Empty<string>();
        return new(new(values[0], values[1], values[2], values[3]), suffix);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int core = Number.CompareTo(other.Number);
        if (core != 0) return core;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            string a = Prerelease[i], b = other.Prerelease[i];
            bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
            string na = a.TrimStart('0'), nb = b.TrimStart('0');
            int comparison = an && bn ? (na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb))
                : an != bn ? an ? -1 : 1 : string.CompareOrdinal(a, b);
            if (comparison != 0) return comparison;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }
}

public sealed class LauncherUpdates : IDisposable
{
    public const string Repository = "https://github.com/LorinAvis/BlockDock";
    public const string Endpoint = "https://api.github.com/repos/LorinAvis/BlockDock/releases?per_page=100";
    const int MaxBytes = 2 * 1024 * 1024;
    readonly HttpClient http;
    readonly string cachePath;
    readonly ReleaseVersion current;
    readonly TimeProvider clock;
    readonly SemaphoreSlim gate = new(1);

    public sealed class Cache
    {
        public int Schema { get; set; } = 1;
        public DateTimeOffset? CheckedAt { get; set; }
        public DateTimeOffset RetryAt { get; set; }
        public string? ETag { get; set; }
        public List<LauncherRelease> Releases { get; set; } = new();
    }

    public LauncherUpdates(HttpMessageHandler? handler = null, string? cachePath = null,
        string? currentVersion = null, bool? currentPreview = null, TimeProvider? clock = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(12);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockDock/" + LauncherVersion.Number);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        this.cachePath = cachePath ?? Path.Combine(Settings.DataPath, "update-cache.json");
        current = ReleaseVersion.Parse(currentVersion ?? LauncherVersion.Number, currentPreview ?? LauncherVersion.IsPreview)
            ?? throw new ArgumentException("无效的启动器版本。");
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<UpdateCheck> CheckAsync(bool includePreviews, bool force, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = ReadCache();
            var now = clock.GetUtcNow();
            UpdateCheck Result(bool cached, string? error = null) => new(Select(cache.Releases, includePreviews), cache.CheckedAt, cached, error);
            if (cache.RetryAt > now)
                return Result(true, "暂时无法检查，请在 " + cache.RetryAt.ToLocalTime().ToString("HH:mm") + " 后重试。");
            if (cache.CheckedAt is { } checkedAt && checkedAt <= now && now - checkedAt < (force ? TimeSpan.FromSeconds(15) : TimeSpan.FromHours(6)))
                return Result(true);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var requestToken = timeout.Token;
                using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
                if (cache.CheckedAt != null && System.Net.Http.Headers.EntityTagHeaderValue.TryParse(cache.ETag, out var etag))
                    request.Headers.IfNoneMatch.Add(etag);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && cache.CheckedAt != null)
                {
                    cache.CheckedAt = now;
                    SaveCache(cache);
                    return Result(false);
                }
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    cache.RetryAt = RetryTime(response, now);
                    SaveCache(cache);
                    return Result(true, "GitHub 暂时限制了请求，请稍后再试。");
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
                await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var data = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
                {
                    if (data.Length + read > MaxBytes) throw new InvalidDataException();
                    data.Write(buffer, 0, read);
                }
                cache.Releases = ParseReleases(data.ToArray());
                cache.ETag = response.Headers.ETag?.ToString();
                cache.CheckedAt = now;
                cache.RetryAt = default;
                SaveCache(cache);
                return Result(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException)
            {
                cache.RetryAt = now.AddMinutes(5);
                SaveCache(cache);
                return Result(true, "检查更新失败，请检查网络后重试。");
            }
        }
        finally { gate.Release(); }
    }

    LauncherRelease? Select(IEnumerable<LauncherRelease> releases, bool includePreviews)
    {
        return releases.Where(r => r != null && ValidPage(r.PageUrl) && (includePreviews || !r.Preview))
            .Select(r => (Release: r, Version: ReleaseVersion.Parse(r.Tag, r.Preview)))
            .Where(r => r.Version != null && (includePreviews || r.Version.Prerelease.Length == 0) && r.Version.CompareTo(current) > 0)
            .OrderByDescending(r => r.Version).Select(r => r.Release).FirstOrDefault();
    }

    static List<LauncherRelease> ParseReleases(byte[] data)
    {
        using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var releases = new List<LauncherRelease>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string Str(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            if (!item.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
            string tag = Str(item, "tag_name"), page = Str(item, "html_url");
            bool preview = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            if (ReleaseVersion.Parse(tag, preview) is null || !ValidPage(page)) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            bool packaged = assets.EnumerateArray().Any(asset => asset.ValueKind == JsonValueKind.Object && Str(asset, "state") == "uploaded"
                && Str(asset, "name").StartsWith("BlockDock-", StringComparison.Ordinal)
                && Str(asset, "name").EndsWith("-Windows-x64.zip", StringComparison.Ordinal)
                && ValidDownload(Str(asset, "browser_download_url"), tag));
            if (!packaged) continue;
            string name = Str(item, "name");
            releases.Add(new(tag, name.Length is > 0 and <= 160 ? name : tag, page, preview));
        }
        return releases;
    }

    public static bool ValidPage(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith("/LorinAvis/BlockDock/releases/tag/", StringComparison.Ordinal)
        && uri.AbsolutePath.Length > "/LorinAvis/BlockDock/releases/tag/".Length;
    static bool ValidDownload(string url, string tag) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith("/LorinAvis/BlockDock/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal);

    static DateTimeOffset RetryTime(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date && date > now) return date;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero) return now + delta;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            && seconds > now.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return now.AddMinutes(30);
    }
    Cache ReadCache()
    {
        try
        {
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > MaxBytes) return new();
            var value = JsonSerializer.Deserialize<Cache>(File.ReadAllText(cachePath));
            return value is { Schema: 1, Releases: not null } ? value : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    void SaveCache(Cache cache)
    {
        try { Settings.AtomicText(cachePath, JsonSerializer.Serialize(cache)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => http.Dispose();
}
