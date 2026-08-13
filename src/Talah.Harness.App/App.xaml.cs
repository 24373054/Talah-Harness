using Microsoft.UI.Xaml;
using Talah.Harness.App.Services;

namespace Talah.Harness.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static Window? MainWindow { get; private set; }
    public static HarnessController Controller { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(Controller);
        MainWindow.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("XAML", e.Exception);
    }
}

internal static class CrashLog
{
    private static readonly SecureDiagnosticLog Log = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talah Harness"));

    public static void Write(string source, Exception exception)
    {
        Log.Write(source, exception);
    }
}

