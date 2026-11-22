using BlockDock;
using System.IO;

static class LaunchReportChecks
{
    public static void Run()
    {
        var root = Directory.CreateTempSubdirectory("BlockDock-report-");
        try
        {
            string logs = root.CreateSubdirectory("logs").FullName;
            string crashes = root.CreateSubdirectory("crash-reports").FullName;
            string latest = Path.Combine(logs, "latest.log");
            string stale = Path.Combine(crashes, "crash-old.txt");
            File.WriteAllText(stale, "Caused by: PreviousCrashException");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(latest, "[main/ERROR] PreviousLogException\n");

            var launch = new LaunchDiagnostics("我的整合包", "Java 版", "1.21.1 · NeoForge", root.FullName, "PlayerFixture", "1");
            var started = DateTimeOffset.UtcNow.AddMilliseconds(-30);
            launch.RecordOutput("<log4j:Message><![CDATA[Caused by: java.lang.IllegalStateException: outer cause]]></log4j:Message>");
            launch.RecordOutput(@"[main/ERROR] Cannot read C:\Users\PrivateTester\logs for PlayerFixture at testuser@example.org");
            launch.RecordOutput("[main/ERROR] Download failed https://example.invalid/?privatevalue=DoNotDisclose");
            launch.RecordOutput("[main/ERROR] --accessToken FakeCredentialForTheTest");
            launch.RecordOutput("[main/ERROR] session_token=TestSessionSecret");
            launch.RecordOutput("[main/ERROR] [CHAT] PlayerFixture: PrivateChatExample");
            for (int i = 0; i < 220; i++) launch.RecordOutput("[thread/INFO] Waiting " + i);
            File.AppendAllText(latest, "[main/INFO] SomeException optional probe\n[thread/ERROR] CurrentLogException\n");
            const string conflict = "java.lang.module.ResolutionException: Modules minecraft and _1._21._1 export package net.minecraft.obfuscate to module jei";
            File.WriteAllText(Path.Combine(crashes, "crash-current.txt"),
                "Description: Game failed\nCaused by: java.lang.IllegalStateException: outer\nCaused by: " + conflict +
                "\n    at java.base/example.resolve(Example.java:12)\nMod ID: examplemod\nMod Version: 1.7\n-- System Details --\n[ERROR] DoNotCollectHardware\n");
            var report = launch.Build("游戏窗口出现前退出", started, 1);
            Check(report.Summary.Contains(conflict) && report.Summary.Contains("两个模块提供了同一个包") &&
                  report.Summary.Contains("1.21.1 · NeoForge") && report.Summary.Contains("退出码 1"),
                  "the deepest module failure is featured with the exact game version and exit code");
            Check(report.Details.Contains("CurrentLogException") && report.Details.Contains("Example.java:12") &&
                  report.Details.Contains("Mod ID: examplemod") && report.Details.Contains("<用户目录>") &&
                  report.Details.Contains("Download failed"), "fresh log lines, Java stack frames, and retained process errors are included");
            foreach (string hidden in new[] { "PreviousCrashException", "PreviousLogException", "optional probe", "DoNotCollectHardware",
                         "PrivateTester", "PlayerFixture", "testuser@example.org", "example.invalid", "DoNotDisclose", "FakeCredentialForTheTest",
                         "TestSessionSecret", "PrivateChatExample" })
                if (report.Text.Contains(hidden, StringComparison.Ordinal)) throw new Exception("report included filtered data: " + hidden);
            string exported = Path.Combine(root.FullName, "export", "report.txt");
            report.Save(exported);
            Check(File.ReadAllText(exported) == report.Text, "TXT export is identical to the report preview");

            var next = new LaunchDiagnostics("另一个实例", "Java 版", "1.20.1", root.FullName);
            File.SetLastWriteTimeUtc(Path.Combine(crashes, "crash-current.txt"), DateTime.UtcNow.AddMinutes(-1));
            var nextStart = DateTimeOffset.UtcNow.AddMilliseconds(-10);
            File.WriteAllText(latest, "Caused by: java.lang.UnsupportedClassVersionError: incompatible class file\n");
            var replaced = next.Build("游戏窗口出现前退出", nextStart, 1);
            Check(replaced.Summary.Contains("当前 Java 版本") && !replaced.Text.Contains("ResolutionException") &&
                  !replaced.Text.Contains("CurrentLogException"), "a replaced latest.log is read from the start without mixing in the previous failure");

            next.LastStep = "验证账号";
            var prep = next.Build("启动准备失败", error: new IOException("outer", new InvalidOperationException("service unavailable")));
            Check(prep.Summary.Contains("service unavailable") && prep.Details.Contains("验证账号") && !prep.Text.Contains("UnsupportedClassVersionError"),
                  "preparation failure uses its inner exception and most recent step, not previous game logs");
            var silent = new LaunchDiagnostics("测试实例", "Windows 基岩版", "1.21").Build("游戏窗口出现前退出", nextStart, 0);
            Check(silent.Summary.Contains("无法确定原因") && silent.Summary.Contains("退出码 0"), "an exit without evidence is reported without guessing a cause");
        }
        finally { root.Delete(recursive: true); }
    }

    static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
