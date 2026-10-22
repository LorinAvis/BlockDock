using System.IO;
using System.Windows;

namespace BlockDock;
public partial class App : Application
{
    readonly HashSet<string> reportedErrors=new(StringComparer.Ordinal);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) => {
            ev.Handled=true;
            string message=SkinAccounts.Redact(ev.Exception.Message);
            if(!reportedErrors.Add(message))return;
            try { Directory.CreateDirectory(Settings.DataPath); File.AppendAllText(Path.Combine(Settings.DataPath,"errors.log"), DateTimeOffset.Now+" "+SkinAccounts.Redact(ev.Exception.ToString())+Environment.NewLine); } catch { }
            MessageBox.Show(message,"BlockDock · 操作未完成",MessageBoxButton.OK,MessageBoxImage.Warning);
            ev.Handled=true;
        };
        new MainWindow().Show();
    }
}
