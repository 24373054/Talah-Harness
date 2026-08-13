using Microsoft.UI.Xaml;

namespace Talah.Harness.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("XAML", e.Exception);
    }
}

internal static class CrashLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Talah Harness",
        "logs");

    public static void Write(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTimeOffset.UtcNow:O}\t{source}\t{exception.GetType().Name}\t{exception.Message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(LogDirectory, "crash.log"), line);
        }
        catch
        {
            // Crash reporting must never replace the original failure.
        }
    }
}

