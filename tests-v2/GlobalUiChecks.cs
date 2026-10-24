using BlockDock;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class GlobalUiChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Flags)!.GetValue(window)!;
    static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(window, value);
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(window, args);
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

    public static void Run(MainWindow window)
    {
        var settings = Field<Settings>(window, "settings");
        var jobs = Field<ObservableCollection<LauncherJob>>(window, "jobs");
        bool ready = Field<bool>(window, "ready"), refreshing = Field<bool>(window, "refreshing");
        Set(window, "ready", false); Set(window, "refreshing", true);
        try
        {
            Check(!settings.CheckUpdatesOnStartup && !DownloadProvider.HasJava && !DownloadProvider.HasBedrock,
                "global UI fixture has no automatic update request or game download provider");
            string root = Path.GetFullPath(Path.Combine(settings.JavaRoot, "versions"));
            var games = new List<GameInstance>();
            var names = new[] { "天空造景 · Fabric", "原版生存", "机械工坊 · Forge", "建筑测试 · NeoForge" };
            var loaders = new[] { "fabric", "vanilla", "forge", "neoforge" };
            for (int n = 0; n < names.Length; n++)
            {
                string folder = Path.Combine(root, "ui-fixture-" + n); Directory.CreateDirectory(folder); SaveIcon(Path.Combine(folder, "pack.png"), n);
                games.Add(new("ui-fixture-" + n, names[n], "1.21.1", loaders[n], folder, folder)
                { ContentSummary = n == 0 ? "42 个模组 · 自动 6 GB 内存" : "0 个模组 · 自动 4 GB 内存", Favorite = n == 0 });
            }
            Set(window, "games", games);
            foreach (string name in new[] { "homeGame", "targetBox", "installedGame" })
            { var picker = Field<ComboBox>(window, name); picker.ItemsSource = games; picker.SelectedIndex = 0; }
            Field<ComboBox>(window, "homeBedrock").ItemsSource = Array.Empty<BedrockPackage>();
            Set(window, "accounts", new List<MicrosoftAccount>());
            Field<ComboBox>(window, "accountBox").ItemsSource = new object[] { "尚未选择账号" };
            Field<ComboBox>(window, "accountBox").SelectedIndex = 0;
            Field<TextBlock>(window, "skinStatus").Text = "登录微软账号后，在这里管理角色与皮肤。";
            Call(window, "RefreshSelectedGame"); Call(window, "FilterGames");
            Field<TextBlock>(window, "gameCount").Text = "共 4 个实例";

            var resources = Enumerable.Range(0, 6).Select(n => new ResourceProject("ui-" + n, "ui-" + n,
                new[] { "造景工具", "流畅渲染", "旅行地图", "光影助手", "建筑指南", "背包整理" }[n],
                "这是本地界面测试用资源，用于查看名称、图标、介绍与安装按钮的布局。", "mod", 9840200 - n * 152400, "BlockDock UI fixture",
                new Uri(Path.Combine(games[n % games.Count].Folder, "pack.png")).AbsoluteUri)).ToArray();
            Field<ListView>(window, "resourceList").ItemsSource = resources;
            // Keep SelectedItem null: selection intentionally requests real project versions.
            var version = new ResourceVersion("ui-version", "ui-0", "稳定版本", "1.2.0", "release", "2026-10-24",
                new JsonObject { ["game_versions"] = new JsonArray("1.21.1"), ["loaders"] = new JsonArray("fabric") });
            Field<ComboBox>(window, "resourceVersions").ItemsSource = new[] { version };
            Field<ComboBox>(window, "resourceVersions").SelectedIndex = 0;
            Field<TextBlock>(window, "resourceStatus").Text = "第 1 页 · 6 项 · 本地界面测试";
            Field<Button>(window, "resourceInstallButton").IsEnabled = true;
            Call(window, "LoadState", "resource", "none", "");
            Set(window, "versions", new List<GameVersion> { new("1.21.1", "release", "2024-08-08"), new("1.20.6", "release", "2024-04-29") });

            jobs.Clear();
            var downloading = new LauncherJob("天空造景整合包") { Category = "modpack", Source = "本地界面测试", State = "进行中", Percent = 62, Indeterminate = false, Icon = games[0].Icon, Destination = Path.Combine(games[0].Folder, "mods") };
            downloading.Transfer(new("fixture-content.jar", Path.Combine(downloading.Destination, "fixture-content.jar"), 62 * 1048576, 100 * 1048576, 7.4 * 1048576, "下载中"));
            jobs.Add(downloading);
            jobs.Add(new LauncherJob("流畅渲染") { Category = "mod", Source = "本地界面测试", Indeterminate = false, Icon = games[2].Icon });
            jobs.Add(new LauncherJob("清新材质包") { Category = "resourcepack", Source = "本地界面测试", State = "已完成", Percent = 100, Finished = true, Indeterminate = false, Detail = "28.4 MB · 下载完成", Icon = games[3].Icon });
            Field<ListView>(window, "jobList").SelectedItem = downloading;

            SearchChecks(window);
            SectionChecks(window);
            foreach ((int width, int height) in new[] { (1120, 780), (1460, 930) })
            {
                foreach (string page in new[] { "home", "games", "resources", "tasks", "account", "settings" })
                {
                    if (page == "resources") { Set(window, "downloadView", "mod"); Set(window, "resourceType", "mod"); Call(window, "Navigate", "download"); }
                    else { Call(window, "Navigate", page); if (page == "settings") Call(window, "ShowSettingsSection", "launch"); }
                    Field<TextBlock>(window, "homeSummary").Text = "4 个 Java 实例\n0 个基岩版实例";
                    Field<TextBlock>(window, "javaDetectionHint").Text = "自动匹配当前游戏需要的 Java 版本。";
                    Snapshot(window, "global-" + page + "-" + width, width, height);
                    Inside(Field<Button>(window, "playButton"), (FrameworkElement)window.Content, width, height, page + " launch action");
                    if (page == "resources") Inside(Field<Button>(window, "resourceInstallButton"), (FrameworkElement)window.Content, width, height, "resource install action");
                }
                Call(window, "Navigate", "settings");
                foreach (string section in new[] { "source", "appearance", "updates" })
                { Call(window, "ShowSettingsSection", section); Snapshot(window, "global-settings-" + section + "-" + width, width, height); }
            }
            ThemeChecks(window);
            Console.WriteLine("GLOBAL UI CHECKS PASSED; synthetic fixtures, no network or gameplay actions");
        }
        finally
        {
            // These visual-only jobs own no worker; do not let them prevent the fixture window closing.
            foreach (var job in jobs) { job.Finished = true; job.Cancellation.Dispose(); }
            jobs.Clear(); Set(window, "ready", ready); Set(window, "refreshing", refreshing);
        }
    }

    static void SearchChecks(MainWindow window)
    {
        foreach (string name in new[] { "gameSearch", "minecraftSearch", "resourceSearch", "bedrockSearch" })
        {
            var input = Field<TextBox>(window, name);
            Check(input.Parent is Grid grid && grid.Children.Contains(input), name + " preserves the original input control inside its search hint");
            var host = (Grid)input.Parent; var hint = host.Children.OfType<TextBlock>().Single(t => t.Text == input.ToolTip?.ToString());
            input.Text = "fixture-search";
            Check(hint.Visibility == Visibility.Collapsed && !hint.IsHitTestVisible, name + " text remains editable and hides the noninteractive hint");
            input.Clear(); Check(hint.Visibility == Visibility.Visible, name + " restores its hint when cleared");
        }
        Field<TextBox>(window, "gameSearch").Text = "天空";
        Check(Field<ListView>(window, "gameList").Items.Count == 1, "instance search still filters its original game collection");
        Field<TextBox>(window, "gameSearch").Clear();
        Field<TextBox>(window, "minecraftSearch").Text = "1.20";
        Check(Field<ListView>(window, "minecraftList").Items.Count == 1, "Java search still filters versions without requesting loaders");
        Field<TextBox>(window, "minecraftSearch").Clear();
        Field<TextBox>(window, "taskSearch").Text = "天空";
        Check(Field<ListView>(window, "jobList").Items.Count == 1, "task search filters the existing collection view");
        Field<TextBox>(window, "taskSearch").Clear();
    }

    static void SectionChecks(MainWindow window)
    {
        var settings = Field<Settings>(window, "settings");
        var pages = Field<Dictionary<string, FrameworkElement>>(window, "pages");
        var sections = Field<Dictionary<string, FrameworkElement>>(window, "settingsSections");
        Check(new[] { "launch", "source", "appearance", "updates", "storage" }.All(sections.ContainsKey), "settings exposes all five distinct sections");
        foreach (string type in new[] { "minecraft", "bedrock" })
        {
            settings.DownloadSourceIntroductionSeen = false; Set(window, "downloadView", type); Call(window, "Navigate", "download");
            Check(pages["settings"].Visibility == Visibility.Visible && pages["download"].Visibility == Visibility.Collapsed && sections["source"].Visibility == Visibility.Visible,
                "first " + type + " download opens the visible download-source section");
        }
        foreach (string id in new[] { "source", "appearance", "updates" })
        {
            Field<Dictionary<string, Button>>(window, "settingsNavigation")[id].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(sections.Count(p => p.Value.Visibility == Visibility.Visible) == 1 && sections[id].Visibility == Visibility.Visible,
                "settings navigation selects only " + id);
        }
        Call(window, "ApplyUpdateResult", new UpdateCheck(new LauncherRelease("v99.0.0", "UI fixture", "https://github.com/LorinAvis/BlockDock/releases/tag/v99.0.0", true), null, true));
        Call(window, "Navigate", "games");
        var notice = Field<Button>(window, "updateNotice"); notice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(notice.Visibility == Visibility.Visible && pages["settings"].Visibility == Visibility.Visible && sections["updates"].Visibility == Visibility.Visible,
            "update notification opens the updates section without a browser or network request");
        Call(window, "ApplyUpdateResult", new UpdateCheck(null, null, false));
    }

    static void ThemeChecks(MainWindow window)
    {
        foreach (string key in new[] { "Primary", "Nav", "Subtle", "Danger", "Chip", "IconButton" }) Check(window.FindResource(key) is Style, "theme exposes " + key);
        foreach (string key in new[] { "CardBackground", "ShellBackground", "WorkspaceBackground" })
            Check(window.FindResource(key) is SolidColorBrush b && b.Color.R < 80 && b.Color.G < 80 && b.Color.B < 80, "wallpaper keeps " + key + " dark");
        var check = Field<CheckBox>(window, "automaticUpdates"); check.ApplyTemplate();
        Check(check.Template.FindName("Box", check) is Border box && box.Background is SolidColorBrush cb && cb.Color.R < 80, "checkbox uses a dark custom template instead of the white native box");
        var input = Field<TextBox>(window, "javaManifestUrl"); input.ApplyTemplate();
        Check(input.Template.FindName("Frame", input) is Border rounded && rounded.CornerRadius.TopLeft > 0, "text inputs use the rounded dark template");
        var combo = Field<ComboBox>(window, "homeGame"); combo.ApplyTemplate();
        Check(combo.Template.FindName("PART_Popup", combo) is Popup && combo.ItemTemplate != null, "version picker retains its popup and icon item template");
        foreach (var bar in Descendants((DependencyObject)window.Content).OfType<ScrollBar>())
        {
            bar.ApplyTemplate();
            Check(bar.Template.FindName("PART_Track", bar) is Track track && track.Thumb?.Template != null,
                "scrollbar retains its functional track and themed thumb");
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(root); n++)
        { var child = VisualTreeHelper.GetChild(root, n); yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    static void Inside(FrameworkElement control, FrameworkElement root, int width, int height, string name)
    {
        var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
        Check(bounds.Width >= 32 && bounds.Height >= 30 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
            name + " is not clipped at " + width + " × " + height);
    }
    static void Snapshot(MainWindow window, string name, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string folder = Path.GetFullPath("artifacts/previews"); Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(stream);
    }
    static void SaveIcon(string path, int variant)
    {
        var pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            int n = (y * 64 + x) * 4; bool accent = x >= 15 && x < 49 && y >= 15 && y < 49;
            pixels[n] = (byte)(accent ? 115 + variant * 20 : 34); pixels[n + 1] = (byte)(accent ? 210 - variant * 25 : 50); pixels[n + 2] = (byte)(accent ? 55 + variant * 35 : 25); pixels[n + 3] = 255;
        }
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 256)));
        using var output = File.Create(path); png.Save(output);
    }
}
