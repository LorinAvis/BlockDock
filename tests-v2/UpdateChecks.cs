using BlockDock;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class UpdateChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            Check(request.RequestUri!.AbsoluteUri == LauncherUpdates.Endpoint && request.Headers.Authorization == null,
                "update check uses public project API without account credentials or game providers");
            return action(request, ct);
        }
    }
    static object Release(string tag, bool preview = false, bool draft = false, bool packaged = true, string? page = null) => new
    {
        tag_name = tag, name = "BlockDock " + tag, prerelease = preview, draft,
        html_url = page ?? LauncherUpdates.Repository + "/releases/tag/" + tag,
        assets = packaged ? new object[] { new { state = "uploaded", name = "BlockDock-" + tag.TrimStart('v') + "-Windows-x64.zip",
            browser_download_url = LauncherUpdates.Repository + "/releases/download/" + tag + "/app.zip" } } : Array.Empty<object>()
    };
    static HttpResponseMessage Response(params object[] releases) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json")
    };
    static string FileAt(string name) => Path.GetFullPath("work/update-checks/" + name + "-" + Guid.NewGuid().ToString("N") + ".json");

    public static void Run(bool live)
    {
        ServiceChecks().GetAwaiter().GetResult();
        if (live) LiveCheck().GetAwaiter().GetResult();
        UiChecks();
        Console.WriteLine("UPDATE CHECKS PASSED");
    }
    static async Task ServiceChecks()
    {
        Check(ReleaseVersion.Parse("v2.10.0")!.CompareTo(ReleaseVersion.Parse("2.9.9")) > 0
            && ReleaseVersion.Parse("2.9.1")!.CompareTo(ReleaseVersion.Parse("2.9.1", true)) > 0
            && ReleaseVersion.Parse("2.9.1-rc.10")!.CompareTo(ReleaseVersion.Parse("2.9.1-rc.2")) > 0
            && ReleaseVersion.Parse("unknown") == null, "versions compare numerically and stable supersedes the same preview");
        var clock = new Clock();
        var handler = new Handler((_, _) => Task.FromResult(Response(Release("v9.0.0", draft: true), Release("v8.0.0", packaged: false),
            Release("v7.0.0", page: "https://other.example/releases/tag/v7.0.0"), Release("v2.10.0", true), Release("v2.9.9"), Release("v2.9.0"))));
        string cache = FileAt("normal");
        using var service = new LauncherUpdates(handler, cache, "2.9.1", true, clock);
        var result = await service.CheckAsync(true, false, default);
        Check(result.Available?.Tag == "v2.10.0" && result.Error == null && !result.Cached,
            "preview channel selects newest packaged release and ignores drafts, missing packages and unrelated links");
        result = await service.CheckAsync(false, false, default);
        Check(result.Available?.Tag == "v2.9.9" && result.Cached && handler.Count == 1, "channel switch reevaluates cached releases without a request");
        await Task.WhenAll(service.CheckAsync(true, true, default), service.CheckAsync(true, true, default));
        Check(handler.Count == 1, "repeated and concurrent checks reuse fresh cache");
        clock.Now = clock.Now.AddHours(7);
        using (var conditional = new LauncherUpdates(new Handler((request, _) =>
        {
            Check(request.Headers.IfNoneMatch.Single().Tag == "\"fixture\"", "cached ETag becomes a conditional request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }), cache, "2.9.1", true, clock))
        {
            var saved = JsonSerializer.Deserialize<LauncherUpdates.Cache>(File.ReadAllText(cache))!;
            saved.ETag = "\"fixture\""; File.WriteAllText(cache, JsonSerializer.Serialize(saved));
            result = await conditional.CheckAsync(true, false, default);
            Check(result.Available?.Tag == "v2.10.0" && result.CheckedAt == clock.Now && result.Error == null, "304 keeps release information and refreshes check time");
        }
        clock.Now = clock.Now.AddHours(7);
        using (var broken = new LauncherUpdates(new Handler((_, _) => throw new HttpRequestException("fixture offline")), cache, "2.9.1", true, clock))
        {
            result = await broken.CheckAsync(true, true, default);
            Check(result.Error != null && result.Available?.Tag == "v2.10.0", "network failure retains known available update without claiming a successful check");
        }
        clock.Now = clock.Now.AddHours(1);
        var limited = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(20));
            return Task.FromResult(response);
        });
        using (var throttled = new LauncherUpdates(limited, cache, "2.9.1", true, clock))
        {
            result = await throttled.CheckAsync(true, true, default);
            await throttled.CheckAsync(true, true, default);
            Check(result.Error != null && limited.Count == 1, "manual checks respect GitHub retry-after and keep old update notice");
        }
        foreach (string body in new[] { "<html>unavailable</html>", "{}", new string('x', 2 * 1024 * 1024 + 1) })
        {
            using var bad = new LauncherUpdates(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })), FileAt("bad"), "2.9.1", true, clock);
            result = await bad.CheckAsync(true, true, default);
            Check(result.Error != null && result.CheckedAt == null && result.Available == null, "invalid or oversized response does not report latest version");
        }
        string corrupt = FileAt("corrupt"); Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!); File.WriteAllText(corrupt, "broken cache");
        using (var fresh = new LauncherUpdates(new Handler((_, _) => Task.FromResult(Response(Release("v2.9.1")))), corrupt, "2.9.1", true, clock))
        {
            result = await fresh.CheckAsync(false, true, default);
            Check(result.Available?.Tag == "v2.9.1", "corrupt cache recovers and stable release upgrades identical preview number");
        }
        using (var current = new LauncherUpdates(new Handler((_, _) => Task.FromResult(Response(Release("v2.9.0"), Release("v2.9.1", true)))), FileAt("current"), "2.9.1", true, clock))
        {
            result = await current.CheckAsync(true, true, default);
            Check(result.Available == null && result.Error == null && result.CheckedAt != null, "same or older preview never creates update prompt");
        }
        using (var cancel = new LauncherUpdates(new Handler(async (_, ct) => { await Task.Delay(10000, ct); return Response(); }), FileAt("cancel"), "2.9.1", true, clock))
        using (var cts = new CancellationTokenSource(50))
        {
            bool cancelled = false;
            try { await cancel.CheckAsync(true, true, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "closing cancels request without storing a false network failure");
        }
        Check(!LauncherUpdates.ValidPage("https://github.com.evil.example/LorinAvis/BlockDock/releases/tag/v9")
            && !LauncherUpdates.ValidPage("file:///C:/app.exe") && !LauncherUpdates.ValidPage("https://github.com:444/LorinAvis/BlockDock/releases/tag/v9"),
            "update links only open the expected HTTPS repository");
    }
    static async Task LiveCheck()
    {
        using var service = new LauncherUpdates(cachePath: FileAt("live"), currentVersion: "2.8.0", currentPreview: true);
        var result = await service.CheckAsync(true, true, default);
        Check(result.Error == null && result.Available != null && LauncherUpdates.ValidPage(result.Available.PageUrl), "live public GitHub release is detected from an older launcher version");
        Console.WriteLine("Live update tag: " + result.Available!.Tag);
    }

    static void UiChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new BlockDock.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        new Settings { CheckUpdatesOnStartup = false, JavaRoot = Path.GetFullPath("work/update-checks/ui-java") }.Save();
        var window = new MainWindow();
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Apply(UpdateCheck result) => typeof(MainWindow).GetMethod("ApplyUpdateResult", flags)!.Invoke(window, new object[] { result });
        try
        {
            Check(Field<Button>("updateNotice").Visibility == Visibility.Collapsed && Field<Button>("downloadUpdateButton").Visibility == Visibility.Collapsed,
                "no update badge or download action before a newer version is known");
            var release = new LauncherRelease("v2.10.0", "BlockDock 2.10.0", LauncherUpdates.Repository + "/releases/tag/v2.10.0", true);
            Apply(new(release, DateTimeOffset.UtcNow, false));
            Check(Field<Button>("updateNotice").Visibility == Visibility.Visible && Field<Button>("downloadUpdateButton").Visibility == Visibility.Visible,
                "new release exposes compact header notice and settings download action");
            Field<Button>("updateNotice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<Dictionary<string, FrameworkElement>>("pages")["settings"].Visibility == Visibility.Visible, "update notice opens settings");
            foreach (int width in new[] { 1120, 1460 })
            {
                int height = width == 1120 ? 780 : 930;
                var visual = (FrameworkElement)window.Content;
                visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string folder = Path.GetFullPath("artifacts/previews"); Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, "updates-" + width + ".png")); encoder.Save(file);
            }
            Apply(new(null, null, false, "检查更新失败，请稍后重试。"));
            Check(Field<TextBlock>("updateDetail").Text.Contains("失败") && !Field<TextBlock>("updateDetail").Text.Contains("最新"),
                "failed first check is clearly distinguished from already up to date");
            Apply(new(null, DateTimeOffset.UtcNow, false));
            Check(Field<Button>("updateNotice").Visibility == Visibility.Collapsed && Field<TextBlock>("updateDetail").Text.Contains("最新"), "successful current-version check hides notice");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
}
