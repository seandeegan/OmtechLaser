using System.IO;
using System.Windows;
using System.Windows.Threading;
using LaserPilot.App.Services;

namespace LaserPilot.App;

public partial class App : Application
{
    /// <summary>Version we just updated from (from --updated-from), if any.</summary>
    public static string? UpdatedFrom { get; private set; }
    /// <summary>A file passed on the command line ("Open with…").</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--updated-from" && i + 1 < e.Args.Length) UpdatedFrom = e.Args[++i];
            else if (File.Exists(e.Args[i])) StartupFile = e.Args[i];
        }

        UpdateService.CleanupAfterUpdate();
        WindowTheme.RegisterDarkTitleBars();
        base.OnStartup(e);

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            // Without a window the process would linger invisibly: explain and quit.
            var path = CrashLog.Write(ex, "Startup");
            MessageBox.Show($"LaserPilot couldn't start.\n\n{ex.Message}\n\n" +
                            (path is null ? "" : $"Details were saved to:\n{path}\n\n") +
                            "Try starting it again. If this keeps happening, rename the folder\n" + AppSettings.Folder +
                            "\nto reset LaserPilot's settings.",
                "LaserPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = CrashLog.Write(e.Exception, "DispatcherUnhandledException");
        // Keep running: the laser may be mid-job and the operator needs the STOP button.
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"Something went wrong, but LaserPilot is still running.\n\n{e.Exception.Message}\n\n" +
                (path is null ? "" : $"Details were saved to:\n{path}\n\n") +
                "If the laser is running and anything looks wrong, press ⛔ STOP.",
                "LaserPilot — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* never let the error dialog itself crash the app */ }
    }
}
