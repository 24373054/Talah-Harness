using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Talah.Harness.App.Models;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Talah.Harness.App;

public sealed partial class MainWindow : Window
{
    private static readonly string ProductDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Talah Harness");

    private readonly string _onboardingMarker = Path.Combine(ProductDataRoot, "state", "onboarding-v1.complete");
    private AppWindow? _appWindow;

    public ObservableCollection<KernelDisplayState> Kernels { get; } =
        new()
        {
            new("codex", "CODEX", "CHECKING", "Discovering Codex App Server", ShellBrushes.Checking),
            new("opencode", "OPENCODE", "CHECKING", "Discovering OpenCode Server", ShellBrushes.Checking),
            new("tlah", "TLAH", "CHECKING", "Loading native TLAH runtime", ShellBrushes.Checking)
        };

    public ObservableCollection<SessionDisplayState> Sessions { get; } = new();

    public ObservableCollection<TraceDisplayState> TraceItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1480, 920));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
            };
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        OnboardingOverlay.Visibility = File.Exists(_onboardingMarker)
            ? Visibility.Collapsed
            : Visibility.Visible;

        await RestoreWorkspaceAsync();
        await ProbeKernelRuntimesAsync();
    }

    private async Task ProbeKernelRuntimesAsync()
    {
        var diagnostics = new List<string>();
        await ProbeExecutableAsync(Kernels[0], "codex", "app-server --help", diagnostics);
        await ProbeExecutableAsync(Kernels[1], "opencode", "serve --help", diagnostics);

        // The native adapter is compiled from the authorized, pinned submodule.
        // Its final availability is replaced by the adapter health result during
        // composition; this source check prevents the shell from claiming an
        // operational runtime before that boundary exists.
        Kernels[2].Status = "SOURCE PINNED";
        Kernels[2].Detail = "TLAH Studio 3ff42e0; adapter initialization pending";
        Kernels[2].IndicatorBrush = ShellBrushes.Checking;
        diagnostics.Add("TLAH: source dependency pinned at 3ff42e0; awaiting adapter health.");

        DiagnosticsText.Text = string.Join(Environment.NewLine, diagnostics);
    }

    private static async Task ProbeExecutableAsync(
        KernelDisplayState state,
        string executable,
        string arguments,
        ICollection<string> diagnostics)
    {
        var path = ResolveExecutable(executable);
        if (path is null)
        {
            state.Status = "NOT INSTALLED";
            state.Detail = $"{executable} was not found on PATH";
            state.IndicatorBrush = ShellBrushes.Missing;
            diagnostics.Add($"{state.Name}: executable not found. Configure or install the pinned runtime.");
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var argument in SplitArguments(arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = ((await stdoutTask) + Environment.NewLine + (await stderrTask)).Trim();
            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Compatible command surface found";

            state.Status = process.ExitCode == 0 ? "AVAILABLE" : "CHECK REQUIRED";
            state.Detail = firstLine.Length > 140 ? firstLine[..140] : firstLine;
            state.IndicatorBrush = process.ExitCode == 0 ? ShellBrushes.Ready : ShellBrushes.Checking;
            diagnostics.Add($"{state.Name}: {state.Detail}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Status = "PROBE FAILED";
            state.Detail = exception.Message;
            state.IndicatorBrush = ShellBrushes.Missing;
            diagnostics.Add($"{state.Name}: probe failed: {exception.GetType().Name}: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            state.Status = "PROBE TIMEOUT";
            state.Detail = "Runtime did not answer within 8 seconds";
            state.IndicatorBrush = ShellBrushes.Missing;
            diagnostics.Add($"{state.Name}: discovery timed out.");
        }
    }

    private static string? ResolveExecutable(string command)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedDirectory = directory.Trim().Trim('"');
            foreach (var extension in extensions.Prepend(string.Empty))
            {
                var candidate = Path.Combine(normalizedDirectory, command + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task RestoreWorkspaceAsync()
    {
        var statePath = Path.Combine(ProductDataRoot, "state", "workspace.txt");
        if (!File.Exists(statePath))
        {
            return;
        }

        var path = await File.ReadAllTextAsync(statePath);
        if (Directory.Exists(path))
        {
            WorkspacePathText.Text = path;
        }
    }

    private async void ChooseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "Use this workspace"
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        WorkspacePathText.Text = folder.Path;
        var stateDirectory = Path.Combine(ProductDataRoot, "state");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, "workspace.txt"), folder.Path);
        ShowInfo("Workspace selected", folder.Path, InfoBarSeverity.Success);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspacePathText.Text == "Choose a folder")
        {
            ShowInfo("Choose a workspace first", "A session must be anchored to an explicit workspace root.", InfoBarSeverity.Warning);
            return;
        }

        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EmptySessionPanel.Visibility = SessionList.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SessionFilter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Filtering is delegated to the durable session query after persistence
        // composition. No sample or in-memory fake session is inserted here.
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Kernel profiles",
            FontFamily = new FontFamily("Bahnschrift SemiCondensed"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        foreach (var kernel in Kernels)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{kernel.Name}  ·  {kernel.Status}{Environment.NewLine}{kernel.Detail}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Settings",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Runtime diagnostics",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    Text = DiagnosticsText.Text
                }
            },
            PrimaryButtonText = "Run checks again",
            CloseButtonText = "Close"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ProbeKernelRuntimesAsync();
        }
    }

    private void SendPrompt_Click(object sender, RoutedEventArgs e) =>
        ShowInfo("No active session", "Create or select a real kernel session before running a prompt.", InfoBarSeverity.Warning);

    private void StopTurn_Click(object sender, RoutedEventArgs e)
    {
    }

    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
    }

    private void RefreshSession_Click(object sender, RoutedEventArgs e)
    {
    }

    private void OpenSessionMenu_Click(object sender, RoutedEventArgs e)
    {
    }

    private void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SendPrompt_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void ReviewSecurity_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Security is kernel-specific",
            Content = new TextBlock
            {
                MaxWidth = 620,
                TextWrapping = TextWrapping.Wrap,
                Text = "Codex can enforce a Windows operating-system sandbox. OpenCode primarily supplies a permission gate unless you configure external isolation. TLAH applies its workspace and tool policies but does not claim a hardened VM boundary. Talah Harness always shows the enforcement owner and actual writable roots before you approve an action."
            },
            CloseButtonText = "Understood"
        };
        await dialog.ShowAsync();
    }

    private void CompleteOnboarding_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_onboardingMarker)!);
        File.WriteAllText(_onboardingMarker, $"completed={DateTimeOffset.UtcNow:O}{Environment.NewLine}telemetry={TelemetryConsentCheckBox.IsChecked == true}");
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _ = OpenKernelSetupAsync();
    }

    private async Task OpenKernelSetupAsync()
    {
        await ProbeKernelRuntimesAsync();
        OpenSettings_Click(this, new RoutedEventArgs());
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        TransientInfoBar.Title = title;
        TransientInfoBar.Message = message;
        TransientInfoBar.Severity = severity;
        TransientInfoBar.IsOpen = true;
    }
}
