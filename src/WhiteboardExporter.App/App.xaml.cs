using System.IO;
using System.Windows;

namespace WhiteboardExporter.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhiteboardExporter");
            Directory.CreateDirectory(folder);
            // Exception text from a browser may include request URLs. Persist
            // only the error type and code, never authentication values.
            var errorCode = args.Exception.GetType().FullName + " HRESULT=0x" + args.Exception.HResult.ToString("X8");
            File.AppendAllText(Path.Combine(folder, "errors.log"), DateTimeOffset.Now.ToString("O") + " " + errorCode + Environment.NewLine);
            MessageBox.Show(args.Exception.Message, "白板导出器", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
