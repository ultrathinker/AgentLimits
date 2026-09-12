using System.IO;
using System.Windows;

namespace AgentLimits;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Statusline-bridge mode for agy: no window, no tray — read stdin,
        // save the payload, echo stdout and exit. Checked before MainWindow is
        // created (StartupUri raises it right after OnStartup returns).
        if (AgyStatusline.TryRun(e.Args))
        {
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        // The window lives in the tray — closing the last window must not kill the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Without this, a crash (e.g. a markup error) just looks like "it didn't start".
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                $"{args.Exception.Message}\n\nDetails: {Path.Combine(AppConfig.Dir, "crash.log")}",
                "AgentLimits", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash(ex);
        };
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "crash.log"),
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            Log.Error("unhandled", ex);
        }
        catch { }
    }
}
