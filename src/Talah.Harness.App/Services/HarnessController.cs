using System.Text.Json;
using Talah.Harness.Adapters.Codex;
using Talah.Harness.Adapters.OpenCode;
using Talah.Harness.Adapters.Tlah;
using Talah.Harness.Application;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;

namespace Talah.Harness.App.Services;

public sealed record ProfileRuntimeState(
    KernelProfile Profile,
    HostedKernelSnapshot? Snapshot,
    string? StartupFailure)
{
    public KernelProfileKey Key => new(Profile.AdapterId, Profile.ProfileId);
    public bool IsOperational => Snapshot?.Health.Availability is KernelAvailability.Ready or KernelAvailability.Degraded;
}

public sealed record HarnessSettings(
    bool OnboardingComplete = false,
    string? WorkspacePath = null,
    bool WorkspaceTrusted = false,
    string? SelectedAdapterId = null,
    IReadOnlyDictionary<string, string>? SelectedModels = null);

public sealed class HarnessController : IAsyncDisposable
{
    public const string HostVersion = "1.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly string _logRoot;
    private readonly string _schemaRoot;
    private readonly KernelHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private Task? _eventTask;
    private long _lastHostSequence;
    private bool _initialized;

    public HarnessController(string? productRoot = null)
    {
        ProductRoot = Path.GetFullPath(productRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talah Harness"));
        _settingsPath = Path.Combine(ProductRoot, "state", "settings.json");
        _logRoot = Path.Combine(ProductRoot, "logs");
        _schemaRoot = Path.Combine(ProductRoot, "schemas");
        var database = new HarnessDatabase(new HarnessDatabaseOptions(
            Path.Combine(ProductRoot, "data", "harness.db"),
            BackupDirectory: Path.Combine(ProductRoot, "data", "backups")));
        _host = new KernelHost(
            [new CodexAdapterFactory(), new OpenCodeAdapterFactory(), new TlahKernelAdapterFactory()],
            database,
            new CanonicalRepository(database));
    }

    public string ProductRoot { get; }
    public HarnessSettings Settings { get; private set; } = new();
    public IReadOnlyList<ProfileRuntimeState> Profiles { get; private set; } = [];
    public WorkspaceDescriptor? Workspace { get; private set; }
    public RecoverySweepResult? LastRecoverySweep => _host.LastRecoverySweep;
    public event EventHandler<DurableKernelEvent>? EventReceived;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        Directory.CreateDirectory(_logRoot);
        Directory.CreateDirectory(_schemaRoot);
        Settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        RestoreWorkspace(Settings);
        await _host.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var states = new List<ProfileRuntimeState>();
        foreach (KernelProfile profile in CreateDefaultProfiles())
        {
            try
            {
                HostedKernelSnapshot snapshot = await _host.StartProfileAsync(
                    profile, HostVersion, _logRoot, _schemaRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
                states.Add(new ProfileRuntimeState(profile, snapshot, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                states.Add(new ProfileRuntimeState(profile, null, SafeFailure(exception, profile.DisplayName)));
            }
        }

        Profiles = states;
        _initialized = true;
        _eventTask = Task.Run(() => PumpEventsAsync(_lifetime.Token), CancellationToken.None);
    }

    public async Task RefreshProfilesAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        IReadOnlyList<HostedKernelSnapshot> snapshots = await _host.GetSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        Profiles = Profiles.Select(state =>
        {
            HostedKernelSnapshot? snapshot = snapshots.FirstOrDefault(item =>
                string.Equals(item.Profile.AdapterId, state.Profile.AdapterId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Profile.ProfileId, state.Profile.ProfileId, StringComparison.OrdinalIgnoreCase));
            return snapshot is null ? state : state with { Snapshot = snapshot, StartupFailure = null };
        }).ToArray();
    }

    public async Task SetWorkspaceAsync(string path, bool trusted, CancellationToken cancellationToken = default)
    {
        Workspace = WorkspacePolicy.CreateDescriptor(path, isTrusted: trusted);
        Settings = Settings with { WorkspacePath = Workspace.RootPath, WorkspaceTrusted = trusted };
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        if (Workspace is null) throw new InvalidOperationException("Choose an existing workspace before completing setup.");
        Settings = Settings with { OnboardingComplete = true };
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        RequireProfile(adapterId, operational: false);
        Settings = Settings with { SelectedAdapterId = adapterId };
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectModelAsync(string adapterId, string modelId, CancellationToken cancellationToken = default)
    {
        var models = new Dictionary<string, string>(Settings.SelectedModels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            [adapterId] = modelId
        };
        Settings = Settings with { SelectedModels = models };
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public string? GetSelectedModel(string adapterId) =>
        Settings.SelectedModels?.TryGetValue(adapterId, out string? model) == true ? model : null;

    public async Task<IReadOnlyList<KernelSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var sessions = new List<KernelSessionSummary>();
        foreach (ProfileRuntimeState profile in Profiles.Where(profile => profile.IsOperational && profile.Snapshot!.Descriptor.Capabilities.CanListSessions))
        {
            string? cursor = null;
            do
            {
                ResultPage<KernelSessionSummary> page = await _host.ListSessionsAsync(profile.Key, new PageRequest(100, cursor), cancellationToken).ConfigureAwait(false);
                sessions.AddRange(page.Items.Where(item => item.Status != SessionStatus.Archived));
                cursor = page.HasMore ? page.NextCursor : null;
            }
            while (cursor is not null);
        }

        return sessions.OrderByDescending(session => session.UpdatedAt).ToArray();
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(
        string adapterId, string? title, string? modelId, string approvalMode, string sandboxMode,
        CancellationToken cancellationToken = default)
    {
        WorkspaceDescriptor workspace = Workspace ?? throw new InvalidOperationException("Choose a workspace before creating a session.");
        ProfileRuntimeState profile = RequireProfile(adapterId, operational: true);
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approvalMode"] = approvalMode,
            ["sandboxMode"] = sandboxMode,
            ["workspaceTrusted"] = workspace.IsTrusted.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return await _host.CreateSessionAsync(profile.Key,
            new CreateSessionRequest(workspace, NullIfWhiteSpace(title), modelId, null, options), cancellationToken).ConfigureAwait(false);
    }

    public Task<KernelSessionSummary> ResumeSessionAsync(SessionRef session, CancellationToken cancellationToken = default) =>
        _host.ResumeSessionAsync(session, cancellationToken);

    public Task<KernelSessionSummary> RenameSessionAsync(SessionRef session, string title, CancellationToken cancellationToken = default) =>
        _host.RenameSessionAsync(session, title, cancellationToken);

    public Task<KernelSessionSummary> ForkSessionAsync(SessionRef session, string? title, CancellationToken cancellationToken = default) =>
        _host.ForkSessionAsync(new ForkSessionRequest(session, Title: NullIfWhiteSpace(title)), cancellationToken);

    public Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default) =>
        _host.ArchiveSessionAsync(session, cancellationToken);

    public async Task<IReadOnlyList<KernelItem>> ReadHistoryAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        var items = new List<KernelItem>();
        string? cursor = null;
        do
        {
            ResultPage<KernelItem> page = await _host.ReadHistoryAsync(session, new PageRequest(100, cursor), cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);
        return items.OrderBy(item => item.CreatedAt).ThenBy(item => item.NativeItemId, StringComparer.Ordinal).ToArray();
    }

    public Task<KernelDiff?> ReadDiffAsync(SessionRef session, CancellationToken cancellationToken = default) =>
        _host.ReadDiffAsync(session, cancellationToken: cancellationToken);

    public async Task<KernelTurn> StartTurnAsync(
        SessionRef session, string prompt, IReadOnlyList<string> referencedPaths,
        string? modelId, string approvalMode, string sandboxMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a nonempty prompt.", nameof(prompt));
        ValidateAttachmentPaths(referencedPaths);
        return await _host.StartTurnAsync(session,
            new TurnInput([new TextContentBlock(prompt.Trim())], referencedPaths),
            new TurnOptions(modelId, approvalMode, sandboxMode, null), cancellationToken).ConfigureAwait(false);
    }

    public Task SteerTurnAsync(SessionRef session, string nativeTurnId, string prompt, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a nonempty steering prompt.", nameof(prompt));
        ValidateAttachmentPaths(paths);
        return _host.SteerTurnAsync(session, nativeTurnId, new TurnInput([new TextContentBlock(prompt.Trim())], paths), cancellationToken);
    }

    public Task CancelTurnAsync(SessionRef session, string nativeTurnId, CancellationToken cancellationToken = default) =>
        _host.CancelTurnAsync(session, nativeTurnId, cancellationToken);

    public Task<AuthenticationState> GetAuthenticationAsync(string adapterId, CancellationToken cancellationToken = default) =>
        _host.GetAuthenticationStateAsync(RequireProfile(adapterId, operational: true).Key, cancellationToken);

    public Task<IReadOnlyList<KernelModel>> ListModelsAsync(string adapterId, CancellationToken cancellationToken = default) =>
        _host.ListModelsAsync(RequireProfile(adapterId, operational: true).Key, cancellationToken);

    public Task<LoginChallenge> BeginLoginAsync(string adapterId, LoginRequest request, CancellationToken cancellationToken = default) =>
        _host.BeginLoginAsync(RequireProfile(adapterId, operational: true).Key, request, cancellationToken);

    public Task CompleteLoginAsync(string adapterId, string loginId, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken = default) =>
        _host.CompleteLoginAsync(RequireProfile(adapterId, operational: true).Key, loginId, parameters, cancellationToken);

    public Task CancelLoginAsync(string adapterId, string loginId, CancellationToken cancellationToken = default) =>
        _host.CancelLoginAsync(RequireProfile(adapterId, operational: true).Key, loginId, cancellationToken);

    public Task ConfigureApiKeyAsync(string adapterId, ApiKeyCredential credential, CancellationToken cancellationToken = default) =>
        _host.ConfigureApiKeyAsync(RequireProfile(adapterId, operational: true).Key, credential, cancellationToken);

    public Task LogoutAsync(string adapterId, CancellationToken cancellationToken = default) =>
        _host.LogoutAsync(RequireProfile(adapterId, operational: true).Key, cancellationToken);

    public Task RespondToPermissionAsync(string adapterId, PermissionResponse response, CancellationToken cancellationToken = default) =>
        _host.RespondToPermissionAsync(RequireProfile(adapterId, operational: true).Key, response, cancellationToken);

    public Task RespondToElicitationAsync(string adapterId, ElicitationResponse response, CancellationToken cancellationToken = default) =>
        _host.RespondToElicitationAsync(RequireProfile(adapterId, operational: true).Key, response, cancellationToken);

    public async Task<bool> IsPermissionPendingAsync(string permissionId, CancellationToken cancellationToken = default) =>
        (await _host.GetPendingPermissionRequestsAsync(cancellationToken).ConfigureAwait(false))
            .Any(item => string.Equals(item.Request.PermissionId, permissionId, StringComparison.Ordinal));

    public async Task<bool> IsElicitationPendingAsync(string requestId, CancellationToken cancellationToken = default) =>
        (await _host.GetPendingElicitationRequestsAsync(cancellationToken).ConfigureAwait(false))
            .Any(item => string.Equals(item.Request.RequestId, requestId, StringComparison.Ordinal));

    public KernelDescriptor? GetDescriptor(string adapterId) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Profile.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))?.Snapshot?.Descriptor;

    public void ValidateAttachmentPaths(IEnumerable<string> paths)
    {
        WorkspaceDescriptor workspace = Workspace ?? throw new InvalidOperationException("Choose a workspace before attaching files.");
        var policy = new WorkspacePolicy();
        policy.ValidateReferencedPaths(workspace, paths);
        foreach (string path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("An attached file no longer exists.", path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_eventTask is not null)
        {
            try { await _eventTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        await _host.DisposeAsync().ConfigureAwait(false);
        _settingsGate.Dispose();
        _lifetime.Dispose();
    }

    public static string SafeFailure(Exception exception, string operation) => exception switch
    {
        DirectoryNotFoundException => $"{operation} could not access its configured data directory.",
        UnauthorizedAccessException => $"{operation} was denied access to a required local resource.",
        _ when exception.GetType().Name.Contains("NotInstalled", StringComparison.Ordinal) => $"{operation} is not installed. Install the supported runtime and restart the profile.",
        _ when exception.GetType().Name.Contains("Incompatible", StringComparison.Ordinal) => $"{operation} is incompatible with this release. Install the supported runtime version.",
        _ => $"{operation} failed ({exception.GetType().Name}). Review redacted runtime diagnostics."
    };

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (DurableKernelEvent durableEvent in _host.WatchEventsAsync(_lastHostSequence, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (durableEvent.HostSequence <= _lastHostSequence) continue;
            _lastHostSequence = durableEvent.HostSequence;
            EventReceived?.Invoke(this, durableEvent);
        }
    }

    private ProfileRuntimeState RequireProfile(string adapterId, bool operational)
    {
        EnsureInitialized();
        ProfileRuntimeState profile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Profile.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Kernel profile '{adapterId}' is not configured.");
        if (operational && !profile.IsOperational)
            throw new InvalidOperationException(profile.StartupFailure ?? profile.Snapshot?.Health.Summary ?? "The kernel is unavailable.");
        return profile;
    }

    private IReadOnlyList<KernelProfile> CreateDefaultProfiles() =>
    [
        new("default", "codex", "Codex", Path.Combine(ProductRoot, "profiles", "codex", "default"), new Dictionary<string, string>(), true),
        new("default", "opencode", "OpenCode", Path.Combine(ProductRoot, "profiles", "opencode", "default"), new Dictionary<string, string>(), true),
        new("default", "tlah", "TLAH", Path.Combine(ProductRoot, "profiles", "tlah", "default"), new Dictionary<string, string>(), true)
    ];

    private void RestoreWorkspace(HarnessSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkspacePath) || !Directory.Exists(settings.WorkspacePath)) return;
        try { Workspace = WorkspacePolicy.CreateDescriptor(settings.WorkspacePath, isTrusted: settings.WorkspaceTrusted); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { Workspace = null; }
    }

    private async Task<HarnessSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath)) return new HarnessSettings();
        try
        {
            await using FileStream stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<HarnessSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new HarnessSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new HarnessSettings();
        }
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, Settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _settingsPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { _settingsGate.Release(); }
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("The harness is still starting.");
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
