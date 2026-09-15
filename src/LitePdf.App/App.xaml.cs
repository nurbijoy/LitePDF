using System.Windows;
using LitePdf.App.Infrastructure;
using LitePdf.Core.Storage;

namespace LitePdf.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            ErrorReporter.Report(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()), "Fatal");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        if (e.Args.Contains("--self-test"))
        {
            Shutdown(RunSelfTest());
            return;
        }

        var settings = AppSettings.Load();
        ThemeManager.Initialize(settings.Theme);

        string? file = e.Args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        var window = new MainWindow(settings, file);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Instantiates every resource in both themes plus all windows and dialogs, so XAML errors that WPF only raises
    /// lazily (on first use) are caught up front. Exit code 0 = pass. Writes results to the log.
    /// </summary>
    private static int RunSelfTest()
    {
        var failures = new List<string>();
        void Check(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetBaseException().Message}");
            }
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            Check($"theme {theme}", () => ThemeManager.Apply(theme));
            foreach (var dictionary in Current.Resources.MergedDictionaries)
                foreach (var key in dictionary.Keys.Cast<object>().ToList())
                    Check($"{theme} resource {key}", () => _ = dictionary[key]);

            var settings = new AppSettings();
            Check($"{theme} MainWindow", () => new MainWindow(settings, null).Close());
            Check($"{theme} MessageDialog", () => Views.MessageDialogProbe.Create());
            Check($"{theme} dialogs", Views.MessageDialogProbe.CreateAll);
        }

        Log.Info(failures.Count == 0 ? "Self-test passed" : "Self-test failed:\n" + string.Join("\n", failures));
        Console.WriteLine(failures.Count == 0 ? "SELF-TEST PASSED" : "SELF-TEST FAILED\n" + string.Join("\n", failures));
        return failures.Count == 0 ? 0 : 1;
    }
}
