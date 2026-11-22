using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlockDock;

public sealed class LaunchErrorReportWindow : Window
{
    readonly TextBlock feedback = new() { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };

    public LaunchErrorReportWindow(LaunchErrorReport report)
    {
        Title = "游戏错误报告 · BlockDock"; Icon = BrandIcon.Image;
        Width = 770; Height = 590; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Background; Foreground = Theme.Text; FontFamily = new("Microsoft YaHei UI"); FontSize = 13;

        var layout = new DockPanel { Margin = new(20) };
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        var actions = new WrapPanel();
        var copy = new Button { Content = "复制报告" };
        copy.SetResourceReference(StyleProperty, "Primary");
        copy.Click += (_, _) => Copy(report.Text, "已复制完整报告。");
        var summary = new Button { Content = "复制摘要" };
        summary.Click += (_, _) => Copy(report.Summary, "已复制摘要。");
        var export = new Button { Content = "导出 TXT…" };
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Title = "导出游戏错误报告", Filter = "文本文件 (*.txt)|*.txt", DefaultExt = ".txt",
                FileName = "BlockDock-游戏错误报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
            if (dialog.ShowDialog(this) != true) return;
            try { report.Save(dialog.FileName); feedback.Text = "已导出到 " + dialog.FileName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { feedback.Text = "保存失败：" + e.Message; }
        };
        actions.Children.Add(copy); actions.Children.Add(summary); actions.Children.Add(export);
        footer.Children.Add(actions); footer.Children.Add(feedback);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), Padding = new(14), Text = report.Text });
        Content = layout;
    }

    void Copy(string text, string success)
    {
        try { Clipboard.SetText(text); feedback.Text = success; }
        catch (ExternalException) { feedback.Text = "剪贴板暂时被占用，可重试或导出 TXT。"; }
    }
}
