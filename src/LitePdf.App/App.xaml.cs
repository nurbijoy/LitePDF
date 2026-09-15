using System.IO;
using System.Windows;

namespace LitePdf.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Command-line file
        string? filePath = null;
        if (e.Args.Length > 0)
        {
            var candidate = e.Args[0];
            if (File.Exists(candidate) && Path.GetExtension(candidate).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                filePath = candidate;
        }

        var mainWindow = new MainWindow();
        if (!string.IsNullOrEmpty(filePath))
        {
            mainWindow.LoadFileOnStartup(filePath);
        }
        mainWindow.Show();
    }
}
