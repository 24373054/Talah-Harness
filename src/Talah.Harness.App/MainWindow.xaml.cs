using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Talah.Harness.App.Models;
using Talah.Harness.App.Services;
using Talah.Harness.Application;
using Talah.Harness.Contracts;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Talah.Harness.App;

public sealed partial class MainWindow : Window
{
    private readonly HarnessController _controller;
    private readonly SemaphoreSlim _interactionGate = new(1, 1);
    private readonly List<KernelSessionSummary> _allSessions = [];
    private AppWindow? _appWindow;
    private SessionDisplayState? _selectedSession;
    private string? _activeTurnId;
    private bool _isBusy;
    private bool _isClosing;
    private string _sessionFilter = string.Empty;
    private string _approvalMode = "on-request";
    private string _sandboxMode = "workspace-write";

    public ObservableCollection<KernelDisplayState> Kernels { get; } =
    [
        new("codex", "CODEX"),
        new("opencode", "OPENCODE"),
        new("tlah", "TLAH")
    ];

    public ObservableCollection<SessionDisplayState> Sessions { get; } = [];
    public ObservableCollection<TraceDisplayState> TraceItems { get; } = [];
    public ObservableCollection<AttachmentDisplayState> Attachments { get; } = [];

    public MainWindow(HarnessController controller)
    {
        _controller = controller;
        InitializeComponent();
        ConfigureWindow();
        _controller.EventReceived += Controller_EventReceived;
        Closed += MainWindow_Closed;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        nint windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1480, 920));
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        await RunOperationAsync(async () =>
        {
            SetBusy(true, "STARTING KERNEL PROFILES");
            await _controller.InitializeAsync();
            ApplyProfiles();
            ApplyWorkspace();
            UpdateOnboarding();
            await RefreshSessionsAsync();
            SetBusy(false, "SELECT A SESSION");
            if (_controller.LastRecoverySweep is { HadInterruptedWork: true } recovery)
            {
                ShowInfo("Interrupted work recovered",
                    $"Marked {recovery.TurnsFailed} turn(s) failed, paused {recovery.SessionsPaused} session(s), abandoned {recovery.ApprovalsAbandoned} permission request(s), and abandoned {recovery.ElicitationsAbandoned} question(s). Refresh or resume a session to continue safely.",
                    InfoBarSeverity.Warning);
            }
        }, "Harness startup");
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosing) return;
        _isClosing = true;
        _controller.EventReceived -= Controller_EventReceived;
        try { await _controller.DisposeAsync(); }
        catch (Exception exception) { CrashLog.Write("shutdown", exception); }
        _interactionGate.Dispose();
    }

    private void Controller_EventReceived(object? sender, DurableKernelEvent durableEvent)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => _ = HandleDurableEventAsync(durableEvent));
    }

    private async Task HandleDurableEventAsync(DurableKernelEvent durable)
    {
        KernelEvent kernelEvent = durable.Event;
        bool isSelectedSession = _selectedSession is not null &&
            string.Equals(kernelEvent.AdapterId, _selectedSession.Summary.Session.AdapterId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kernelEvent.ProfileId, _selectedSession.Summary.Session.ProfileId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kernelEvent.NativeSessionId, _selectedSession.Summary.Session.NativeSessionId, StringComparison.Ordinal);

        if (isSelectedSession)
        {
            if (kernelEvent.Data is ContentDeltaEventData delta)
            {
                string identity = kernelEvent.NativeItemId ?? $"delta-{kernelEvent.NativeTurnId}-{delta.Channel}";
                TraceDisplayState? existing = TraceItems.LastOrDefault(item => item.ItemId == identity);
                if (existing is null) TraceItems.Add(TraceProjection.FromEvent(durable));
                else existing.Append(delta.Delta);
            }
            else if (kernelEvent.Data is ItemEventData itemData)
            {
                int index = IndexOfTrace(itemData.Item.NativeItemId);
                TraceDisplayState projected = TraceProjection.FromItem(itemData.Item, kernelEvent.AdapterId);
                if (index < 0) TraceItems.Add(projected);
                else TraceItems[index] = projected;
            }
            else
            {
                TraceItems.Add(TraceProjection.FromEvent(durable));
            }

            TraceScrollViewer.ChangeView(null, TraceScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }

        if (kernelEvent.Data is UsageEventData usage && isSelectedSession)
            UsageText.Text = TraceProjection.FormatUsage(usage);

        if (kernelEvent.Data is DiagnosticEventData diagnostic)
            AppendDiagnostic(kernelEvent.AdapterId, diagnostic.Diagnostic);

        if (kernelEvent.Kind == KernelEventKind.TurnStarted && isSelectedSession)
        {
            _activeTurnId = kernelEvent.NativeTurnId;
            UpdateComposerState();
        }
        else if (kernelEvent.Kind is KernelEventKind.TurnCompleted or KernelEventKind.TurnFailed or KernelEventKind.TurnCancelled && isSelectedSession)
        {
            _activeTurnId = null;
            UpdateComposerState();
            await RefreshDiffCoreAsync();
            await RefreshSessionsAsync(preserveSelection: true);
        }

        if (kernelEvent.Data is PermissionEventData permission)
            await PresentPermissionAsync(kernelEvent.AdapterId, permission.Request);
        else if (kernelEvent.Data is ElicitationEventData elicitation)
            await PresentElicitationAsync(kernelEvent.AdapterId, elicitation.Request);

        if (kernelEvent.Kind is KernelEventKind.AdapterStatusChanged or KernelEventKind.CapabilityChanged or KernelEventKind.AuthenticationChanged)
        {
            await _controller.RefreshProfilesAsync();
            ApplyProfiles();
        }
    }

    private void ApplyProfiles()
    {
        foreach (KernelDisplayState kernel in Kernels)
        {
            ProfileRuntimeState? state = _controller.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Profile.AdapterId, kernel.AdapterId, StringComparison.OrdinalIgnoreCase));
            if (state is not null) kernel.Apply(state);
        }
        DiagnosticsText.Text = BuildDiagnosticsText();
        OnboardingHealthText.Text = string.Join(Environment.NewLine, Kernels.Select(kernel =>
            $"{kernel.Name,-9} {kernel.Status,-13} {kernel.Detail}"));
    }

    private string BuildDiagnosticsText()
    {
        var lines = new List<string>();
        foreach (ProfileRuntimeState profile in _controller.Profiles)
        {
            string name = profile.Profile.DisplayName;
            if (profile.StartupFailure is not null)
            {
                lines.Add($"{name}: {profile.StartupFailure}");
                continue;
            }
            HostedKernelSnapshot snapshot = profile.Snapshot!;
            lines.Add($"{name}: {snapshot.Health.Availability} · {snapshot.Health.Summary}");
            lines.Add($"  protocol: {snapshot.Descriptor.ProtocolKind}");
            lines.Add($"  security: {snapshot.Descriptor.Security.HumanReadableSummary}");
            foreach (KernelDiagnostic diagnostic in snapshot.Health.Diagnostics)
            {
                lines.Add($"  {diagnostic.Severity}: {diagnostic.Code} · {diagnostic.Message}");
                if (!string.IsNullOrWhiteSpace(diagnostic.Remediation)) lines.Add($"    action: {diagnostic.Remediation}");
            }
            if (snapshot.EventPumpFailure is not null) lines.Add("  event stream: stopped; restart this profile after reviewing adapter diagnostics.");
        }
        if (_controller.LastRecoverySweep is { HadInterruptedWork: true } recovery)
        {
            lines.Add($"Recovery: turns failed {recovery.TurnsFailed}; sessions paused {recovery.SessionsPaused}; permissions abandoned {recovery.ApprovalsAbandoned}; questions abandoned {recovery.ElicitationsAbandoned}.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ApplyWorkspace()
    {
        string text = _controller.Workspace?.RootPath ?? "Choose a folder";
        WorkspacePathText.Text = text;
        OnboardingWorkspaceText.Text = _controller.Workspace is null
            ? "Workspace not selected"
            : $"Approved workspace: {_controller.Workspace.RootPath}\nTrusted: {_controller.Workspace.IsTrusted}";
        InspectorWorkspaceText.Text = _controller.Workspace is null ? "No workspace selected" : _controller.Workspace.RootPath;
    }

    private void UpdateOnboarding() =>
        OnboardingOverlay.Visibility = _controller.Settings.OnboardingComplete ? Visibility.Collapsed : Visibility.Visible;

    private async Task RefreshSessionsAsync(bool preserveSelection = false)
    {
        SessionRef? selectedRef = preserveSelection ? _selectedSession?.Summary.Session : null;
        IReadOnlyList<KernelSessionSummary> sessions = await _controller.ListSessionsAsync();
        _allSessions.Clear();
        _allSessions.AddRange(sessions);
        ApplySessionFilter();
        if (selectedRef is not null)
        {
            SessionDisplayState? match = Sessions.FirstOrDefault(item => SameSession(item.Summary.Session, selectedRef));
            if (match is not null) SessionList.SelectedItem = match;
        }
    }

    private void ApplySessionFilter()
    {
        SessionRef? selected = _selectedSession?.Summary.Session;
        IEnumerable<KernelSessionSummary> filtered = _allSessions.Where(session => string.IsNullOrWhiteSpace(_sessionFilter) ||
            session.Title.Contains(_sessionFilter, StringComparison.CurrentCultureIgnoreCase) ||
            (session.Preview?.Contains(_sessionFilter, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            session.Session.AdapterId.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase));
        Sessions.Clear();
        foreach (KernelSessionSummary summary in filtered) Sessions.Add(new SessionDisplayState(summary));
        if (selected is not null)
            SessionList.SelectedItem = Sessions.FirstOrDefault(item => SameSession(item.Summary.Session, selected));
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
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunOperationAsync(async () =>
        {
            await _controller.SetWorkspaceAsync(folder.Path, trusted: false);
            ApplyWorkspace();
            ShowInfo("Workspace selected", "Attachments and kernel file access are constrained to this approved root.", InfoBarSeverity.Success);
        }, "Choose workspace");
    }

    private async void CompleteOnboarding_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            await _controller.CompleteOnboardingAsync();
            UpdateOnboarding();
            await ShowKernelSettingsAsync();
        }, "Complete setup");
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Workspace is null)
        {
            ShowInfo("Choose a workspace", "A session must be anchored to an approved workspace root.", InfoBarSeverity.Warning);
            return;
        }
        ProfileRuntimeState[] available = _controller.Profiles.Where(profile => profile.IsOperational).ToArray();
        if (available.Length == 0)
        {
            ShowInfo("No kernel is available", "Open kernel setup for installation and authentication guidance.", InfoBarSeverity.Error);
            return;
        }

        var kernelBox = new ComboBox { Header = "Kernel", MinWidth = 320, ItemsSource = available, DisplayMemberPath = "Profile.DisplayName" };
        kernelBox.SelectedItem = available.FirstOrDefault(profile => profile.Profile.AdapterId == _controller.Settings.SelectedAdapterId) ?? available[0];
        var titleBox = new TextBox { Header = "Title", PlaceholderText = "Optional session title" };
        var modelText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"] };
        void UpdateModelText() => modelText.Text = kernelBox.SelectedItem is ProfileRuntimeState profile
            ? $"Model: {_controller.GetSelectedModel(profile.Profile.AdapterId) ?? "kernel default"}. Change this in kernel setup."
            : "Model: kernel default";
        kernelBox.SelectionChanged += (_, _) => UpdateModelText();
        UpdateModelText();
        var approvalBox = new ComboBox { Header = "Approval policy", ItemsSource = new[] { "on-request", "untrusted", "never" }, SelectedItem = _approvalMode };
        var sandboxBox = new ComboBox { Header = "Sandbox", ItemsSource = new[] { "workspace-write", "read-only", "danger-full-access" }, SelectedItem = _sandboxMode };
        var trustedBox = new CheckBox { Content = "Trust this workspace for the selected kernel", IsChecked = _controller.Workspace.IsTrusted };
        var content = new StackPanel { Spacing = 10, Children = { kernelBox, titleBox, modelText, approvalBox, sandboxBox, trustedBox } };
        var dialog = CreateDialog("Create session", content, "Create", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunOperationAsync(async () =>
        {
            ProfileRuntimeState profile = (ProfileRuntimeState)kernelBox.SelectedItem;
            _approvalMode = approvalBox.SelectedItem?.ToString() ?? "on-request";
            _sandboxMode = sandboxBox.SelectedItem?.ToString() ?? "workspace-write";
            if (_controller.Workspace!.IsTrusted != (trustedBox.IsChecked == true))
                await _controller.SetWorkspaceAsync(_controller.Workspace.RootPath, trustedBox.IsChecked == true);
            await _controller.SelectAdapterAsync(profile.Profile.AdapterId);
            KernelSessionSummary created = await _controller.CreateSessionAsync(profile.Profile.AdapterId, titleBox.Text,
                _controller.GetSelectedModel(profile.Profile.AdapterId), _approvalMode, _sandboxMode);
            await RefreshSessionsAsync();
            SessionList.SelectedItem = Sessions.FirstOrDefault(item => SameSession(item.Summary.Session, created.Session));
        }, "Create session");
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionDisplayState selected)
        {
            _selectedSession = null;
            EmptySessionPanel.Visibility = Visibility.Visible;
            TraceItems.Clear();
            UpdateInspector(null);
            UpdateComposerState();
            return;
        }
        _selectedSession = selected;
        EmptySessionPanel.Visibility = Visibility.Collapsed;
        await RunOperationAsync(async () =>
        {
            KernelSessionSummary resumed = await _controller.ResumeSessionAsync(selected.Summary.Session);
            _selectedSession = new SessionDisplayState(resumed);
            await LoadSelectedSessionAsync();
        }, "Open session");
    }

    private async Task LoadSelectedSessionAsync()
    {
        if (_selectedSession is null) return;
        TraceItems.Clear();
        IReadOnlyList<KernelItem> history = await _controller.ReadHistoryAsync(_selectedSession.Summary.Session);
        foreach (KernelItem item in history) TraceItems.Add(TraceProjection.FromItem(item, _selectedSession.Summary.Session.AdapterId));
        ConversationTitleText.Text = _selectedSession.Title;
        ConversationMetaText.Text = $"{_selectedSession.KernelName} / {_selectedSession.StatusLabel} / {_selectedSession.SessionId}";
        UpdateInspector(_selectedSession);
        UpdateComposerState();
        await RefreshDiffCoreAsync();
        TraceScrollViewer.ChangeView(null, TraceScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void SessionFilter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _sessionFilter = sender.Text.Trim();
        ApplySessionFilter();
    }

    private async void RefreshSession_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            await RefreshSessionsAsync(preserveSelection: true);
            if (_selectedSession is not null) await LoadSelectedSessionAsync();
        }, "Refresh session");
    }

    private async void OpenSessionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSession is null) return;
        KernelDescriptor? descriptor = _controller.GetDescriptor(_selectedSession.Summary.Session.AdapterId);
        var actions = new List<string> { "Rename" };
        if (descriptor?.Capabilities.CanForkSessions == true) actions.Add("Fork");
        if (descriptor?.Capabilities.CanArchiveSessions == true) actions.Add("Archive");
        var actionBox = new ComboBox { Header = "Action", ItemsSource = actions, SelectedIndex = 0, MinWidth = 280 };
        var titleBox = new TextBox { Header = "New title", Text = _selectedSession.Title };
        actionBox.SelectionChanged += (_, _) => titleBox.Visibility = actionBox.SelectedItem?.ToString() == "Archive" ? Visibility.Collapsed : Visibility.Visible;
        var dialog = CreateDialog("Session actions", new StackPanel { Spacing = 10, Children = { actionBox, titleBox } }, "Continue", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string action = actionBox.SelectedItem?.ToString() ?? "Rename";
        if (action == "Archive")
        {
            var confirm = CreateDialog("Archive session?", new TextBlock
            {
                Text = "The kernel will archive this session. It will leave the active ledger but remain in the durable store.",
                TextWrapping = TextWrapping.Wrap
            }, "Archive", "Cancel");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await RunOperationAsync(async () =>
        {
            SessionRef session = _selectedSession.Summary.Session;
            if (action == "Rename") await _controller.RenameSessionAsync(session, RequireTitle(titleBox.Text));
            else if (action == "Fork") await _controller.ForkSessionAsync(session, RequireTitle(titleBox.Text));
            else await _controller.ArchiveSessionAsync(session);
            if (action == "Archive")
            {
                _selectedSession = null;
                SessionList.SelectedItem = null;
            }
            await RefreshSessionsAsync(preserveSelection: action != "Archive");
        }, action + " session");
    }

    private async void SendPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSession is null || string.IsNullOrWhiteSpace(PromptTextBox.Text)) return;
        string prompt = PromptTextBox.Text;
        string[] paths = Attachments.Select(item => item.FullPath).ToArray();
        await RunOperationAsync(async () =>
        {
            SetBusy(true, _activeTurnId is null ? "STARTING TURN" : "STEERING ACTIVE TURN");
            if (_activeTurnId is not null)
            {
                await _controller.SteerTurnAsync(_selectedSession.Summary.Session, _activeTurnId, prompt, paths);
            }
            else
            {
                TraceItems.Add(new TraceDisplayState($"local-{Guid.NewGuid():N}", "PROMPT", "User instruction", prompt,
                    DateTimeOffset.UtcNow, "\uE8BD", ShellBrushes.ForAdapter(_selectedSession.Summary.Session.AdapterId), false));
                KernelTurn turn = await _controller.StartTurnAsync(_selectedSession.Summary.Session, prompt, paths,
                    _controller.GetSelectedModel(_selectedSession.Summary.Session.AdapterId), _approvalMode, _sandboxMode);
                _activeTurnId = turn.NativeTurnId;
            }
            PromptTextBox.Text = string.Empty;
            Attachments.Clear();
            SetBusy(false, "TURN RUNNING");
            UpdateComposerState();
        }, _activeTurnId is null ? "Run prompt" : "Steer turn");
    }

    private async void StopTurn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSession is null || _activeTurnId is null) return;
        string turnId = _activeTurnId;
        await RunOperationAsync(async () =>
        {
            SetBusy(true, "CANCELLING TURN");
            await _controller.CancelTurnAsync(_selectedSession.Summary.Session, turnId);
            _activeTurnId = null;
            SetBusy(false, "TURN CANCELLED");
            UpdateComposerState();
        }, "Cancel turn");
    }

    private async void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Workspace is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        string[] paths = files.Select(file => file.Path).ToArray();
        try
        {
            _controller.ValidateAttachmentPaths(paths);
            foreach (string path in paths.Where(path => Attachments.All(item => !string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase))))
                Attachments.Add(new AttachmentDisplayState(path));
        }
        catch (Exception exception)
        {
            ShowInfo("Attachment denied", HarnessController.SafeFailure(exception, "Workspace validation"), InfoBarSeverity.Error);
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AttachmentDisplayState attachment)
            Attachments.Remove(attachment);
    }

    private void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SendPrompt_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && _activeTurnId is not null)
        {
            StopTurn_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void UpdateComposerState()
    {
        bool hasSession = _selectedSession is not null;
        KernelDescriptor? descriptor = hasSession ? _controller.GetDescriptor(_selectedSession!.Summary.Session.AdapterId) : null;
        bool canSteer = descriptor?.Capabilities.CanSteerActiveTurn == true;
        PromptTextBox.IsEnabled = hasSession && !_isBusy && (_activeTurnId is null || canSteer);
        AttachButton.IsEnabled = PromptTextBox.IsEnabled;
        SendButton.IsEnabled = PromptTextBox.IsEnabled;
        SendButton.Content = _activeTurnId is null ? "Run" : "Steer";
        StopButton.Visibility = _activeTurnId is not null && descriptor?.Capabilities.CanCancelTurn == true ? Visibility.Visible : Visibility.Collapsed;
        ComposerStatusText.Text = !hasSession ? "NO ACTIVE KERNEL" : _isBusy ? "WORKING" : _activeTurnId is null
            ? $"{_selectedSession!.KernelName} READY" : canSteer ? "ACTIVE / STEERING AVAILABLE" : "ACTIVE / WAIT OR STOP";
    }

    private void UpdateInspector(SessionDisplayState? session)
    {
        if (session is null)
        {
            InspectorKernelName.Text = "None selected";
            InspectorKernelState.Text = "OFFLINE";
            InspectorSessionText.Text = "No session selected";
            SecurityInfoBar.Title = "No kernel selected";
            SecurityInfoBar.Message = "Select a real session to inspect its enforcement boundary.";
            CapabilityList.ItemsSource = null;
            RefreshDiffButton.IsEnabled = false;
            return;
        }
        KernelDescriptor? descriptor = _controller.GetDescriptor(session.Summary.Session.AdapterId);
        ProfileRuntimeState? runtime = _controller.Profiles.FirstOrDefault(item => item.Profile.AdapterId == session.Summary.Session.AdapterId);
        InspectorKernelName.Text = descriptor?.DisplayName ?? session.KernelName;
        InspectorKernelState.Text = runtime?.Snapshot?.Health.Availability.ToString().ToUpperInvariant() ?? "UNAVAILABLE";
        InspectorSessionText.Text = $"{session.StatusLabel}\nprofile {session.Summary.Session.ProfileId}\nsession {session.SessionId}";
        InspectorWorkspaceText.Text = _controller.Workspace?.RootPath ?? "Workspace binding unavailable";
        if (descriptor is not null)
        {
            SecurityDescriptor security = descriptor.Security;
            SecurityInfoBar.Title = $"{security.EnforcementKind} · {security.EnforcementOwner}";
            SecurityInfoBar.Message = $"{security.HumanReadableSummary} Writable roots: {(security.WritableRoots.Count == 0 ? "none reported" : string.Join(", ", security.WritableRoots))}. Network restricted: {security.NetworkRestricted}. Process restricted: {security.ProcessRestricted}. Host verified: {security.IsVerifiedByHost}.";
            CapabilityList.ItemsSource = CapabilityLabels(descriptor.Capabilities);
        }
        RefreshDiffButton.IsEnabled = descriptor?.Capabilities.CanReturnDiffs == true;
    }

    private static IReadOnlyList<string> CapabilityLabels(KernelCapabilities capabilities)
    {
        var labels = new List<string>();
        if (capabilities.CanAuthenticate) labels.Add("Official authentication flow");
        if (capabilities.CanUseApiKey) labels.Add("Protected API-key setup");
        if (capabilities.CanResumeSessions) labels.Add("Resume sessions");
        if (capabilities.CanForkSessions) labels.Add("Fork sessions");
        if (capabilities.CanSteerActiveTurn) labels.Add("Steer active turn");
        if (capabilities.CanCancelTurn) labels.Add("Cancel turn");
        if (capabilities.CanApproveTools) labels.Add("Tool permission decisions");
        if (capabilities.CanAmendToolInput) labels.Add("Amended tool input");
        if (capabilities.CanReturnDiffs) labels.Add("Workspace diffs");
        if (capabilities.CanUseSubagents) labels.Add("Native subagents");
        return labels;
    }

    private async void RefreshDiff_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(RefreshDiffCoreAsync, "Refresh diff");

    private async Task RefreshDiffCoreAsync()
    {
        if (_selectedSession is null || _controller.GetDescriptor(_selectedSession.Summary.Session.AdapterId)?.Capabilities.CanReturnDiffs != true)
        {
            DiffText.Text = "The selected kernel does not provide a diff for this session.";
            return;
        }
        KernelDiff? diff = await _controller.ReadDiffAsync(_selectedSession.Summary.Session);
        DiffText.Text = diff is null || string.IsNullOrWhiteSpace(diff.UnifiedDiff)
            ? "No file changes reported by the selected kernel."
            : diff.UnifiedDiff + (diff.IsTruncated ? Environment.NewLine + "Diff truncated by kernel." : string.Empty);
    }

    private async void KernelStatus_Click(object sender, RoutedEventArgs e)
    {
        string? adapterId = (sender as FrameworkElement)?.DataContext is KernelDisplayState kernel ? kernel.AdapterId : null;
        await ShowKernelSettingsAsync(adapterId);
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e) => await ShowKernelSettingsAsync();

    private async Task ShowKernelSettingsAsync(string? initialAdapterId = null)
    {
        KernelDisplayState[] states = Kernels.ToArray();
        var kernelBox = new ComboBox { Header = "Kernel profile", ItemsSource = states, DisplayMemberPath = "Name", MinWidth = 380 };
        kernelBox.SelectedItem = states.FirstOrDefault(item => item.AdapterId == initialAdapterId) ??
            states.FirstOrDefault(item => item.AdapterId == _controller.Settings.SelectedAdapterId) ?? states[0];
        var healthText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var authText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var providerBox = new TextBox { Header = "Provider ID", PlaceholderText = "Required for provider login or API key" };
        var baseUriBox = new TextBox { Header = "Optional provider base URI", PlaceholderText = "https://…" };
        var secretBox = new PasswordBox { Header = "API key", PasswordRevealMode = PasswordRevealMode.Peek };
        AutomationProperties.SetName(secretBox, "Provider API key");
        var modelBox = new ComboBox { Header = "Model", MinWidth = 380, DisplayMemberPath = "DisplayName" };
        var refreshButton = new Button { Content = "Refresh health and models", MinHeight = 44 };
        var logoutButton = new Button { Content = "Log out / remove provider auth", MinHeight = 44 };
        var setupButton = new Button { Content = "Show first-run guide", MinHeight = 44 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refreshButton, logoutButton, setupButton } };
        var content = new StackPanel { Spacing = 10, Children = { kernelBox, healthText, authText, providerBox, baseUriBox, secretBox, modelBox, actions } };

        async Task RefreshAsync()
        {
            if (kernelBox.SelectedItem is not KernelDisplayState selected) return;
            await _controller.RefreshProfilesAsync();
            ApplyProfiles();
            KernelDisplayState state = Kernels.First(item => item.AdapterId == selected.AdapterId);
            healthText.Text = $"{state.Status}: {state.Detail}";
            bool operational = state.Runtime?.IsOperational == true;
            providerBox.IsEnabled = operational;
            baseUriBox.IsEnabled = operational && state.Runtime?.Snapshot?.Descriptor.Capabilities.CanUseApiKey == true;
            secretBox.IsEnabled = baseUriBox.IsEnabled;
            logoutButton.IsEnabled = operational;
            modelBox.IsEnabled = operational;
            if (!operational)
            {
                authText.Text = "Authentication and models are unavailable until this kernel is installed and healthy.";
                modelBox.ItemsSource = null;
                return;
            }
            try
            {
                AuthenticationState auth = await _controller.GetAuthenticationAsync(selected.AdapterId);
                authText.Text = $"Authentication: {auth.Status}{(string.IsNullOrWhiteSpace(auth.AccountLabel) ? string.Empty : " · " + auth.AccountLabel)}{(string.IsNullOrWhiteSpace(auth.Message) ? string.Empty : Environment.NewLine + auth.Message)}";
                IReadOnlyList<KernelModel> models = await _controller.ListModelsAsync(selected.AdapterId);
                modelBox.ItemsSource = models;
                string? chosen = _controller.GetSelectedModel(selected.AdapterId);
                modelBox.SelectedItem = models.FirstOrDefault(model => model.ModelId == chosen) ?? models.FirstOrDefault(model => model.IsDefault) ?? models.FirstOrDefault();
            }
            catch (Exception exception)
            {
                authText.Text = HarnessController.SafeFailure(exception, "Authentication and model refresh");
                modelBox.ItemsSource = null;
            }
        }

        kernelBox.SelectionChanged += async (_, _) => await RefreshAsync();
        refreshButton.Click += async (_, _) => await RefreshAsync();
        logoutButton.Click += async (_, _) =>
        {
            if (kernelBox.SelectedItem is not KernelDisplayState selected) return;
            try { await _controller.LogoutAsync(selected.AdapterId); await RefreshAsync(); }
            catch (Exception exception) { authText.Text = HarnessController.SafeFailure(exception, "Logout"); }
        };
        setupButton.Click += (_, _) => OnboardingOverlay.Visibility = Visibility.Visible;
        await RefreshAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Kernel setup",
            Content = new ScrollViewer { MaxHeight = 590, Content = content },
            PrimaryButtonText = "Save API key",
            SecondaryButtonText = "Sign in",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (kernelBox.SelectedItem is not KernelDisplayState kernel) return;
        if (modelBox.SelectedItem is KernelModel model) await _controller.SelectModelAsync(kernel.AdapterId, model.ModelId);
        if (result == ContentDialogResult.Primary)
        {
            string secret = secretBox.Password;
            secretBox.Password = string.Empty;
            if (string.IsNullOrWhiteSpace(providerBox.Text) || string.IsNullOrWhiteSpace(secret))
            {
                ShowInfo("Provider and API key required", "Enter both values. The key is sent directly to the adapter and is never written to app settings or diagnostics.", InfoBarSeverity.Warning);
                return;
            }
            Uri? baseUri = null;
            if (!string.IsNullOrWhiteSpace(baseUriBox.Text) && (!Uri.TryCreate(baseUriBox.Text, UriKind.Absolute, out baseUri) || baseUri.Scheme is not ("http" or "https")))
            {
                ShowInfo("Invalid provider URI", "Use an absolute HTTP or HTTPS URI.", InfoBarSeverity.Warning);
                return;
            }
            await RunOperationAsync(async () =>
            {
                await _controller.ConfigureApiKeyAsync(kernel.AdapterId, new ApiKeyCredential(providerBox.Text.Trim(), secret, baseUri));
                ShowInfo("API key configured", "The adapter accepted the credential through its protected provider flow.", InfoBarSeverity.Success);
            }, "Configure API key");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await BeginInteractiveLoginAsync(kernel.AdapterId, providerBox.Text.Trim());
        }
    }

    private async Task BeginInteractiveLoginAsync(string adapterId, string providerId)
    {
        await RunOperationAsync(async () =>
        {
            AuthenticationState state = await _controller.GetAuthenticationAsync(adapterId);
            AuthenticationMethod method = state.SupportedMethods.Contains(AuthenticationMethod.Browser)
                ? AuthenticationMethod.Browser
                : state.SupportedMethods.Contains(AuthenticationMethod.DeviceCode) ? AuthenticationMethod.DeviceCode
                : throw new InvalidOperationException("This kernel supports API keys only.");
            IReadOnlyDictionary<string, string>? parameters = adapterId == "opencode"
                ? string.IsNullOrWhiteSpace(providerId) ? throw new InvalidOperationException("OpenCode login requires a provider ID.") : new Dictionary<string, string> { ["providerId"] = providerId }
                : null;
            LoginChallenge challenge = await _controller.BeginLoginAsync(adapterId, new LoginRequest(method, parameters));
            if (challenge.VerificationUri is not null) await Launcher.LaunchUriAsync(challenge.VerificationUri);
            var challengeText = new TextBlock
            {
                Text = challenge.Instructions + (string.IsNullOrWhiteSpace(challenge.UserCode) ? string.Empty : $"\n\nDevice code: {challenge.UserCode}"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            if (adapterId == "opencode")
            {
                var codeBox = new TextBox { Header = "OAuth callback code", PlaceholderText = "Paste the code returned by the provider" };
                var callbackDialog = CreateDialog("Complete OpenCode login", new StackPanel { Spacing = 10, Children = { challengeText, codeBox } }, "Complete login", "Cancel");
                if (await callbackDialog.ShowAsync() == ContentDialogResult.Primary)
                    await _controller.CompleteLoginAsync(adapterId, challenge.LoginId, new Dictionary<string, string> { ["code"] = codeBox.Text.Trim() });
                else await _controller.CancelLoginAsync(adapterId, challenge.LoginId);
            }
            else
            {
                var challengeDialog = CreateDialog("Complete kernel login", challengeText, "Done", "Cancel");
                if (await challengeDialog.ShowAsync() != ContentDialogResult.Primary)
                    await _controller.CancelLoginAsync(adapterId, challenge.LoginId);
            }
        }, "Kernel login");
    }

    private async void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Runtime diagnostics",
            Content = new ScrollViewer { MaxHeight = 520, Content = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono"), FontSize = 11,
                IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Text = BuildDiagnosticsText()
            } },
            PrimaryButtonText = "Run checks again",
            CloseButtonText = "Close"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await RunOperationAsync(async () => { await _controller.RefreshProfilesAsync(); ApplyProfiles(); }, "Refresh diagnostics");
    }

    private async Task PresentPermissionAsync(string adapterId, PermissionRequest request)
    {
        await _interactionGate.WaitAsync();
        try
        {
            if (!await _controller.IsPermissionPendingAsync(request.PermissionId)) return;
            var choiceBox = new ComboBox { Header = "Decision", ItemsSource = request.Choices, DisplayMemberPath = "Label", MinWidth = 420 };
            choiceBox.SelectedItem = request.Choices.FirstOrDefault(choice => choice.Kind == PermissionDecisionKind.Deny) ?? request.Choices.FirstOrDefault();
            var impactText = new TextBlock
            {
                Text = request.Impacts.Count == 0 ? "The adapter reported no structured resource impacts." : string.Join(Environment.NewLine,
                    request.Impacts.Select(impact => $"{impact.RiskLevel.ToUpperInvariant()} · {impact.Operation} · {impact.Kind} · {impact.Target}{(string.IsNullOrWhiteSpace(impact.Detail) ? string.Empty : "\n  " + impact.Detail)}")),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Cascadia Mono"), FontSize = 11
            };
            var amendedBox = new TextBox { Header = "Amended input JSON", AcceptsReturn = true, MinHeight = 110, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            choiceBox.SelectionChanged += (_, _) => amendedBox.Visibility = choiceBox.SelectedItem is PermissionChoice choice && choice.AllowsAmendedInput ? Visibility.Visible : Visibility.Collapsed;
            var content = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock { Text = request.Reason ?? "The kernel requires a permission decision.", TextWrapping = TextWrapping.Wrap },
                impactText, choiceBox, amendedBox
            } };
            var dialog = CreateDialog(request.Title, content, "Respond", "Deny");
            ContentDialogResult result = await dialog.ShowAsync();
            PermissionChoice selected = result == ContentDialogResult.Primary && choiceBox.SelectedItem is PermissionChoice chosen
                ? chosen : request.Choices.FirstOrDefault(choice => choice.Kind == PermissionDecisionKind.Deny)
                    ?? throw new InvalidOperationException("The adapter did not provide a denial choice.");
            JsonElement? amended = null;
            if (selected.AllowsAmendedInput)
            {
                if (string.IsNullOrWhiteSpace(amendedBox.Text)) throw new InvalidOperationException("The selected choice requires amended JSON input.");
                using JsonDocument document = JsonDocument.Parse(amendedBox.Text);
                amended = document.RootElement.Clone();
            }
            await _controller.RespondToPermissionAsync(adapterId, new PermissionResponse(request.PermissionId, selected.ChoiceId, amended));
        }
        catch (Exception exception)
        {
            ShowInfo("Permission response failed", HarnessController.SafeFailure(exception, "Permission response"), InfoBarSeverity.Error);
        }
        finally { _interactionGate.Release(); }
    }

    private async Task PresentElicitationAsync(string adapterId, ElicitationRequest request)
    {
        await _interactionGate.WaitAsync();
        try
        {
            if (!await _controller.IsElicitationPendingAsync(request.RequestId)) return;
            string schemaType = GetSchemaType(request.Schema);
            FrameworkElement editor = schemaType switch
            {
                "number" or "integer" => new NumberBox { Header = "Value", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact },
                "boolean" => new ToggleSwitch { Header = "Value", OffContent = "False", OnContent = "True" },
                "object" or "array" => new TextBox { Header = "JSON value", AcceptsReturn = true, MinHeight = 130, Text = schemaType == "array" ? "[]" : "{}", FontFamily = new FontFamily("Cascadia Mono") },
                _ => new TextBox { Header = "Response", AcceptsReturn = false }
            };
            var dialog = CreateDialog(request.Title, new StackPanel { Spacing = 10, Children =
            {
                new TextBlock { Text = request.Prompt, TextWrapping = TextWrapping.Wrap }, editor
            } }, "Submit", "Cancel");
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                await _controller.RespondToElicitationAsync(adapterId, new ElicitationResponse(request.RequestId, true));
                return;
            }
            JsonElement value = ReadElicitationValue(editor, schemaType);
            await _controller.RespondToElicitationAsync(adapterId, new ElicitationResponse(request.RequestId, false, value));
        }
        catch (Exception exception)
        {
            ShowInfo("Question response failed", HarnessController.SafeFailure(exception, "Question response"), InfoBarSeverity.Error);
        }
        finally { _interactionGate.Release(); }
    }

    private static string GetSchemaType(JsonElement? schema)
    {
        if (schema is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String)
            return type.GetString() ?? "string";
        return "string";
    }

    private static JsonElement ReadElicitationValue(FrameworkElement editor, string type) => type switch
    {
        "number" => JsonSerializer.SerializeToElement(((NumberBox)editor).Value),
        "integer" => JsonSerializer.SerializeToElement(checked((long)((NumberBox)editor).Value)),
        "boolean" => JsonSerializer.SerializeToElement(((ToggleSwitch)editor).IsOn),
        "object" or "array" => ParseJson(((TextBox)editor).Text),
        _ => JsonSerializer.SerializeToElement(((TextBox)editor).Text)
    };

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async void ReviewSecurity_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDialog("Security is kernel-specific", new TextBlock
        {
            MaxWidth = 620,
            TextWrapping = TextWrapping.Wrap,
            Text = "Codex can report a Windows operating-system sandbox. OpenCode supplies a permission gate unless external isolation is configured. TLAH applies workspace and tool policies without claiming a hardened virtual-machine boundary. The inspector always names the enforcement owner, verified status, writable roots, network policy, and process policy reported by the active adapter."
        }, null, "Understood");
        await dialog.ShowAsync();
    }

    private void AppendDiagnostic(string adapterId, KernelDiagnostic diagnostic)
    {
        string line = $"{adapterId.ToUpperInvariant()}: {diagnostic.Severity} · {diagnostic.Code} · {diagnostic.Message}";
        DiagnosticsText.Text = string.IsNullOrWhiteSpace(DiagnosticsText.Text) ? line : DiagnosticsText.Text + Environment.NewLine + line;
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        ComposerStatusText.Text = status;
        UpdateComposerState();
    }

    private async Task RunOperationAsync(Func<Task> action, string operation)
    {
        try { await action(); }
        catch (Exception exception)
        {
            SetBusy(false, "ACTION FAILED");
            ShowInfo(operation + " failed", HarnessController.SafeFailure(exception, operation), InfoBarSeverity.Error);
        }
    }

    private ContentDialog CreateDialog(string title, object content, string? primaryText, string closeText) => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primaryText,
        CloseButtonText = closeText,
        DefaultButton = primaryText is null ? ContentDialogButton.Close : ContentDialogButton.Primary
    };

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        TransientInfoBar.Title = title;
        TransientInfoBar.Message = message;
        TransientInfoBar.Severity = severity;
        TransientInfoBar.IsOpen = true;
    }

    private int IndexOfTrace(string itemId)
    {
        for (int index = 0; index < TraceItems.Count; index++)
            if (TraceItems[index].ItemId == itemId) return index;
        return -1;
    }

    private static string RequireTitle(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Enter a nonempty session title.") : value.Trim();

    private static bool SameSession(SessionRef left, SessionRef right) =>
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NativeSessionId, right.NativeSessionId, StringComparison.Ordinal);
}
