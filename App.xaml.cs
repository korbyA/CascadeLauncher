using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CascadeLauncher.Services;

namespace CascadeLauncher;

public partial class App : Application
{
    public static string LauncherRoot { get; } = AppContext.BaseDirectory;
    public static string RuntimeDir { get; } = Path.Combine(LauncherRoot, "runtime");
    public static string LogsDir { get; } = Path.Combine(LauncherRoot, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(LogsDir);

        Logger.Initialize(Path.Combine(LogsDir, "launcher.log"));
        Logger.Info($"Cascade Launcher Alpha starting (root={LauncherRoot})");

        // Surface unhandled exceptions to the user instead of silently dying.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Cascade Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Error("Unhandled exception", ex);
    }
}
