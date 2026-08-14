using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;

namespace Talah.Harness.Application;

public sealed record KernelProfileKey(string AdapterId, string ProfileId);

public sealed record HostedKernelSnapshot(
    KernelProfile Profile,
    KernelDescriptor Descriptor,
    KernelHealth Health,
    string? EventPumpFailure);

public sealed record DurableKernelEvent(long HostSequence, string NativeEventId, KernelEvent Event);
public sealed record DurablePermissionRequest(PermissionRequest Request, string Status, DateTimeOffset CreatedAt);
public sealed record DurableElicitationRequest(ElicitationRequest Request, string Status, DateTimeOffset CreatedAt);

public sealed class KernelHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AdapterShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EventDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyDictionary<string, IKernelAdapterFactory> _factories;
    private readonly HarnessDatabase _database;
    private readonly CanonicalRepository _repository;
    private readonly KernelEventProjection _projection;
    private readonly WorkspacePolicy _workspacePolicy;
    private readonly ConcurrentDictionary<KernelProfileKey, HostedKernel> _kernels = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ChangeSignal _eventChanged = new();
    private bool _initialized;
    private bool _disposed;

    public RecoverySweepResult? LastRecoverySweep { get; private set; }

    public KernelHost(
        IEnumerable<IKernelAdapterFactory> factories,
        HarnessDatabase database,
        CanonicalRepository repository,
        WorkspacePolicy? workspacePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories.ToDictionary(factory => factory.AdapterId, StringComparer.OrdinalIgnoreCase);
        if (_factories.Count == 0) throw new ArgumentException("At least one kernel adapter factory is required.", nameof(factories));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _projection = new KernelEventProjection(repository);
        _workspacePolicy = workspacePolicy ?? new WorkspacePolicy();
    }

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) throw new InvalidOperationException("The kernel host is already initialized.");
            DatabaseInitializationResult result = await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await RepairEventProjectionsAsync(cancellationToken).ConfigureAwait(false);
            LastRecoverySweep = await _repository.RecoverInterruptedOperationsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<HostedKernelSnapshot> StartProfileAsync(
        KernelProfile profile,
        string hostVersion,
        string logRoot,
        string schemaRoot,
        bool diagnosticMode = false,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ValidateProfile(profile);
        if (!_factories.TryGetValue(profile.AdapterId, out IKernelAdapterFactory? factory))
            throw new KeyNotFoundException($"No adapter factory is registered for '{profile.AdapterId}'.");
        KernelProfileKey key = Key(profile);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_kernels.ContainsKey(key)) throw new InvalidOperationException($"Profile '{profile.AdapterId}/{profile.ProfileId}' is already running.");
            IKernelAdapter? adapter = null;
            try
            {
                adapter = await factory.CreateAsync(profile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(adapter.AdapterId, profile.AdapterId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The adapter factory returned a different adapter identity.");
                await adapter.InitializeAsync(new KernelInitializationContext(hostVersion, profile, logRoot, schemaRoot, diagnosticMode), cancellationToken).ConfigureAwait(false);
                var hosted = new HostedKernel(profile, adapter, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                if (!_kernels.TryAdd(key, hosted)) throw new InvalidOperationException("The profile was started concurrently.");
                if (CanPumpEvents(adapter.Descriptor.Availability))
                    hosted.EventPump = Task.Run(() => PumpEventsAsync(hosted), CancellationToken.None);
                await _repository.UpsertAdapterProfileAsync(new StoredAdapterProfile(
                    profile.AdapterId, profile.ProfileId, profile.DisplayName, profile.DataRoot, profile.IsDefault, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                return await SnapshotAsync(hosted, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (adapter is not null) await adapter.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopProfileAsync(KernelProfileKey key, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        HostedKernel? hosted;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_kernels.TryRemove(key, out hosted)) return;
        }
        finally
        {
            _lifecycle.Release();
        }

        await StopHostedKernelAsync(hosted, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HostedKernelSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var snapshots = new List<HostedKernelSnapshot>();
        foreach (HostedKernel? hosted in _kernels.Values.OrderBy(value => value.Profile.AdapterId).ThenBy(value => value.Profile.ProfileId))
            snapshots.Add(await SnapshotAsync(hosted, cancellationToken).ConfigureAwait(false));
        return snapshots;
    }

    public Task<AuthenticationState> GetAuthenticationStateAsync(KernelProfileKey key, CancellationToken cancellationToken = default) =>
        Get(key).GetAuthenticationStateAsync(cancellationToken);

    public Task<LoginChallenge> BeginLoginAsync(KernelProfileKey key, LoginRequest request, CancellationToken cancellationToken = default) =>
        Get(key).BeginLoginAsync(request, cancellationToken);

    public Task CancelLoginAsync(KernelProfileKey key, string loginId, CancellationToken cancellationToken = default) =>
        Get(key).CancelLoginAsync(loginId, cancellationToken);

    public Task CompleteLoginAsync(
        KernelProfileKey key,
        string loginId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default) =>
        Get(key) is IInteractiveLoginCompletionAdapter completion
            ? completion.CompleteLoginAsync(loginId, parameters, cancellationToken)
            : throw new NotSupportedException($"Kernel '{key.AdapterId}' completes login without a host callback.");

    public Task ConfigureApiKeyAsync(KernelProfileKey key, ApiKeyCredential credential, CancellationToken cancellationToken = default) =>
        Get(key).ConfigureApiKeyAsync(credential, cancellationToken);

    public Task LogoutAsync(KernelProfileKey key, CancellationToken cancellationToken = default) =>
        Get(key).LogoutAsync(cancellationToken);

    public Task<IReadOnlyList<KernelModel>> ListModelsAsync(KernelProfileKey key, CancellationToken cancellationToken = default) =>
        Get(key).ListModelsAsync(cancellationToken);

    public async Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(KernelProfileKey key, PageRequest page, CancellationToken cancellationToken = default)
    {
        ResultPage<KernelSessionSummary> result = await Get(key).ListSessionsAsync(page, cancellationToken).ConfigureAwait(false);
        foreach (KernelSessionSummary session in result.Items)
        {
            ValidateOwnership(key, session.Session);
            await _repository.UpsertSessionAsync(
                await PreserveWorkspaceBindingAsync(session, null, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(KernelProfileKey key, CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkspaceDescriptor workspace = _workspacePolicy.Normalize(request.Workspace);
        await _repository.UpsertWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        IKernelAdapter adapter = Get(key);
        (string? approvalMode, string? sandboxMode) = NormalizeSecurityOptions(
            adapter.Descriptor.Security,
            request.Options?.GetValueOrDefault("approvalMode"),
            request.Options?.GetValueOrDefault("sandboxMode"));
        KernelSessionSummary result = await adapter.CreateSessionAsync(request with { Workspace = workspace }, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(key, result.Session);
        result = ApplySessionSecurityMetadata(result, approvalMode, sandboxMode);
        result = await PreserveWorkspaceBindingAsync(result, workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await _repository.UpsertSessionAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<KernelSessionSummary> ResumeSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        KernelSessionSummary result = await Get(Key(session)).ResumeSessionAsync(session, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(Key(session), result.Session);
        result = await PreserveWorkspaceBindingAsync(result, session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await _repository.UpsertSessionAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<KernelSessionSummary> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        KernelProfileKey key = Key(request.Session);
        KernelSessionSummary result = await Get(key).ForkSessionAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(key, result.Session);
        StoredSession? parent = await _repository.GetSessionAsync(request.Session, cancellationToken).ConfigureAwait(false);
        result = PreserveSessionSecurityMetadata(result, parent?.Summary.Metadata);
        result = await PreserveWorkspaceBindingAsync(
            result,
            request.Session.WorkspaceId ?? parent?.Summary.Session.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        await _repository.UpsertSessionAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        IKernelAdapter adapter = Get(Key(session));
        await adapter.ArchiveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        StoredSession? stored = await _repository.GetSessionAsync(session, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            await _repository.UpsertSessionAsync(stored.Summary with
            {
                Status = SessionStatus.Archived,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<KernelSessionSummary> RenameSessionAsync(
        SessionRef session,
        string title,
        CancellationToken cancellationToken = default)
    {
        IKernelAdapter adapter = Get(Key(session));
        if (adapter is not ISessionRenameAdapter naming)
            throw new NotSupportedException($"Kernel '{session.AdapterId}' does not support session renaming.");
        KernelSessionSummary result = await naming.RenameSessionAsync(session, title, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(Key(session), result.Session);
        result = await PreserveWorkspaceBindingAsync(result, session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await _repository.UpsertSessionAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<KernelTurn> StartTurnAsync(SessionRef session, TurnInput input, TurnOptions options, CancellationToken cancellationToken = default)
    {
        TurnInputPolicy.Validate(input);
        StoredSession stored = await RequireStoredSessionAsync(session, cancellationToken).ConfigureAwait(false);
        WorkspaceDescriptor workspace = await RequireWorkspaceAsync(stored.Summary.Session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        _workspacePolicy.ValidateReferencedPaths(workspace, input.ReferencedPaths);
        IKernelAdapter adapter = Get(Key(session));
        (string? approvalMode, string? sandboxMode) = NormalizeSecurityOptions(
            adapter.Descriptor.Security,
            options.ApprovalMode,
            options.SandboxMode);
        KernelTurn turn = await adapter.StartTurnAsync(
            session,
            input,
            options with { ApprovalMode = approvalMode, SandboxMode = sandboxMode },
            cancellationToken).ConfigureAwait(false);
        ValidateOwnership(Key(session), turn.Session);
        await _repository.UpsertTurnAsync(stored.HostSessionId, turn.NativeTurnId, turn.Status, turn.StartedAt, cancellationToken: cancellationToken).ConfigureAwait(false);
        return turn;
    }

    public async Task SteerTurnAsync(SessionRef session, string nativeTurnId, TurnInput input, CancellationToken cancellationToken = default)
    {
        TurnInputPolicy.Validate(input);
        StoredSession stored = await RequireStoredSessionAsync(session, cancellationToken).ConfigureAwait(false);
        WorkspaceDescriptor workspace = await RequireWorkspaceAsync(stored.Summary.Session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        _workspacePolicy.ValidateReferencedPaths(workspace, input.ReferencedPaths);
        await Get(Key(session)).SteerTurnAsync(session, nativeTurnId, input, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelTurnAsync(SessionRef session, string nativeTurnId, CancellationToken cancellationToken = default) =>
        Get(Key(session)).CancelTurnAsync(session, nativeTurnId, cancellationToken);

    public async Task RespondToPermissionAsync(KernelProfileKey key, PermissionResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        StoredApproval pending = await _repository.GetApprovalAsync(response.PermissionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Permission request '{response.PermissionId}' is not in the durable approval ledger.");
        if (!string.Equals(pending.AdapterId, key.AdapterId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.ProfileId, key.ProfileId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The permission request belongs to a different kernel profile.");
        if (!string.Equals(pending.Status, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The permission request was already resolved.");
        JsonElement responseJson = JsonSerializer.SerializeToElement(response, JsonOptions);
        await _repository.UpsertApprovalAsync(pending with
        {
            Status = "responding",
            Response = responseJson
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await Get(key).RespondToPermissionAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _repository.UpsertApprovalAsync(pending with
            {
                Status = "indeterminate",
                Response = responseJson,
                ResolvedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _repository.UpsertApprovalAsync(pending with
        {
            Status = "resolved",
            Response = responseJson,
            ResolvedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RespondToElicitationAsync(KernelProfileKey key, ElicitationResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        StoredElicitation pending = await _repository.GetElicitationAsync(response.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Elicitation request '{response.RequestId}' is not in the durable interaction ledger.");
        if (!string.Equals(pending.AdapterId, key.AdapterId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.ProfileId, key.ProfileId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The elicitation request belongs to a different kernel profile.");
        if (!string.Equals(pending.Status, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The elicitation request was already resolved.");
        JsonElement responseJson = JsonSerializer.SerializeToElement(response, JsonOptions);
        await _repository.UpsertElicitationAsync(pending with
        {
            Status = "responding",
            Response = responseJson
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await Get(key).RespondToElicitationAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _repository.UpsertElicitationAsync(pending with
            {
                Status = "indeterminate",
                Response = responseJson,
                ResolvedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _repository.UpsertElicitationAsync(pending with
        {
            Status = "resolved",
            Response = responseJson,
            ResolvedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DurablePermissionRequest>> GetPendingPermissionRequestsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredApproval> stored = await _repository.ListPendingApprovalsAsync(cancellationToken).ConfigureAwait(false);
        return stored.Select(item => new DurablePermissionRequest(
            item.Request.Deserialize<PermissionRequest>(JsonOptions)
                ?? throw new InvalidDataException($"Stored permission request '{item.ApprovalId}' is invalid."),
            item.Status,
            item.CreatedAt)).ToArray();
    }

    public async Task<IReadOnlyList<DurableElicitationRequest>> GetPendingElicitationRequestsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredElicitation> stored = await _repository.ListPendingElicitationsAsync(cancellationToken).ConfigureAwait(false);
        return stored.Select(item => new DurableElicitationRequest(
            item.Request.Deserialize<ElicitationRequest>(JsonOptions)
                ?? throw new InvalidDataException($"Stored elicitation request '{item.RequestId}' is invalid."),
            item.Status,
            item.CreatedAt)).ToArray();
    }

    public async Task<ResultPage<KernelItem>> ReadHistoryAsync(SessionRef session, PageRequest page, CancellationToken cancellationToken = default)
    {
        ResultPage<KernelItem> result = await Get(Key(session)).ReadHistoryAsync(session, page, cancellationToken).ConfigureAwait(false);
        StoredSession stored = await RequireStoredSessionAsync(session, cancellationToken).ConfigureAwait(false);
        foreach (KernelItem item in result.Items)
            await _repository.UpsertItemAsync(stored.HostSessionId, null, item, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<KernelDiff?> ReadDiffAsync(SessionRef session, string? nativeTurnOrItemId = null, CancellationToken cancellationToken = default) =>
        Get(Key(session)).ReadDiffAsync(session, nativeTurnOrItemId, cancellationToken);

    public async IAsyncEnumerable<DurableKernelEvent> WatchEventsAsync(
        long afterHostSequence = 0,
        string? adapterId = null,
        string? profileId = null,
        string? nativeSessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureReady();
        long cursor = afterHostSequence;
        while (true)
        {
            long observedVersion = _eventChanged.Version;
            IReadOnlyList<StoredCanonicalEvent> events = await _repository.GetEventsAfterAsync(cursor, 256, adapterId, profileId, nativeSessionId, cancellationToken).ConfigureAwait(false);
            if (events.Count != 0)
            {
                foreach (StoredCanonicalEvent item in events)
                {
                    cursor = item.HostSequence;
                    yield return new DurableKernelEvent(item.HostSequence, item.NativeEventId, item.Event);
                }

                continue;
            }

            await _eventChanged.WaitForChangeAsync(observedVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Task[] stops = _kernels.Keys.ToArray().Select(async key =>
        {
            try { await StopProfileAsync(key).ConfigureAwait(false); }
            catch (Exception) { }
        }).ToArray();
        await Task.WhenAll(stops).ConfigureAwait(false);

        _shutdown.Cancel();
        _disposed = true;
        _shutdown.Dispose();
        _lifecycle.Dispose();
    }

    private static async Task StopHostedKernelAsync(HostedKernel hosted, CancellationToken cancellationToken)
    {
        Exception? disposalFailure = null;
        Task disposal = hosted.Adapter.DisposeAsync().AsTask();
        try
        {
            await disposal.WaitAsync(AdapterShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            disposalFailure = new TimeoutException(
                $"Kernel adapter shutdown exceeded {AdapterShutdownTimeout.TotalSeconds:0} seconds.",
                exception);
            hosted.Cancellation.Cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            hosted.Cancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        if (hosted.EventPump is not null)
        {
            try
            {
                await hosted.EventPump.WaitAsync(EventDrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                hosted.Cancellation.Cancel();
                try { await hosted.EventPump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                hosted.Cancellation.Cancel();
                throw;
            }
            catch (Exception)
            {
                // Event-pump failures are already retained as redacted profile diagnostics.
            }
        }

        hosted.Cancellation.Cancel();
        hosted.Cancellation.Dispose();
        if (disposalFailure is not null)
            throw new InvalidOperationException("The kernel adapter did not shut down cleanly; inspect redacted diagnostics.", disposalFailure);
    }

    private async Task PumpEventsAsync(HostedKernel hosted)
    {
        try
        {
            await foreach (KernelEvent? kernelEvent in hosted.Adapter.WatchEventsAsync(hosted.Cancellation.Token).ConfigureAwait(false))
            {
                if (!string.Equals(kernelEvent.AdapterId, hosted.Profile.AdapterId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(kernelEvent.ProfileId, hosted.Profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The adapter emitted an event for a different profile.");
                await _projection.PersistAsync(kernelEvent, hosted.Cancellation.Token).ConfigureAwait(false);
                _eventChanged.Pulse();
            }
        }
        catch (OperationCanceledException) when (hosted.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            hosted.EventPumpFailure = $"{exception.GetType().Name}: the adapter event pump stopped; inspect redacted adapter diagnostics.";
        }
    }

    private async Task RepairEventProjectionsAsync(CancellationToken cancellationToken)
    {
        long cursor = 0;
        while (true)
        {
            IReadOnlyList<StoredCanonicalEvent> events = await _repository.GetEventsAfterAsync(cursor, 512, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (events.Count == 0) return;
            foreach (StoredCanonicalEvent item in events)
            {
                await _projection.ApplyAsync(item.Event, cancellationToken).ConfigureAwait(false);
                cursor = item.HostSequence;
            }
        }
    }

    private static async Task<HostedKernelSnapshot> SnapshotAsync(HostedKernel hosted, CancellationToken cancellationToken)
    {
        KernelHealth health = await hosted.Adapter.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        if (hosted.EventPumpFailure is not null)
        {
            health = health with
            {
                Availability = KernelAvailability.Degraded,
                Diagnostics = health.Diagnostics.Append(new KernelDiagnostic(
                    "host.event-pump.stopped", DiagnosticSeverity.Error, hosted.EventPumpFailure,
                    "Restart this kernel profile after reviewing adapter diagnostics.")).ToArray()
            };
        }

        return new HostedKernelSnapshot(hosted.Profile, hosted.Adapter.Descriptor, health, hosted.EventPumpFailure);
    }

    private IKernelAdapter Get(KernelProfileKey key)
    {
        EnsureReady();
        return _kernels.TryGetValue(key, out HostedKernel? hosted)
            ? hosted.Adapter
            : throw new KeyNotFoundException($"Kernel profile '{key.AdapterId}/{key.ProfileId}' is not running.");
    }

    private async Task<StoredSession> RequireStoredSessionAsync(SessionRef session, CancellationToken cancellationToken) =>
        await _repository.GetSessionAsync(session, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Session '{session.NativeSessionId}' is not in the canonical store.");

    private async Task<WorkspaceDescriptor> RequireWorkspaceAsync(string? workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId is null) throw new InvalidOperationException("The session has no durable workspace binding.");
        return (await _repository.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false))?.Workspace
            ?? throw new InvalidOperationException("The session's durable workspace binding is missing.");
    }

    private async Task<KernelSessionSummary> PreserveWorkspaceBindingAsync(
        KernelSessionSummary summary,
        string? preferredWorkspaceId,
        CancellationToken cancellationToken)
    {
        StoredSession? existing = await _repository.GetSessionAsync(summary.Session, cancellationToken).ConfigureAwait(false);
        string? workspaceId = preferredWorkspaceId ?? existing?.Summary.Session.WorkspaceId;
        KernelSessionSummary preserved = PreserveSessionSecurityMetadata(summary, existing?.Summary.Metadata);
        return workspaceId is null || preserved.Session.WorkspaceId is not null
            ? preserved
            : preserved with { Session = preserved.Session with { WorkspaceId = workspaceId } };
    }

    private static KernelSessionSummary ApplySessionSecurityMetadata(
        KernelSessionSummary summary,
        string? approvalMode,
        string? sandboxMode)
    {
        if (approvalMode is null && sandboxMode is null)
            return summary;
        var metadata = summary.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(summary.Metadata, StringComparer.Ordinal);
        if (approvalMode is not null) metadata[SessionSecurityMetadata.ApprovalMode] = approvalMode;
        if (sandboxMode is not null) metadata[SessionSecurityMetadata.SandboxMode] = sandboxMode;
        return summary with { Metadata = metadata };
    }

    private static KernelSessionSummary PreserveSessionSecurityMetadata(
        KernelSessionSummary summary,
        IReadOnlyDictionary<string, string>? existingMetadata)
    {
        if (existingMetadata is null)
            return summary;
        var metadata = summary.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(summary.Metadata, StringComparer.Ordinal);
        foreach (string key in new[] { SessionSecurityMetadata.ApprovalMode, SessionSecurityMetadata.SandboxMode })
        {
            if (!metadata.ContainsKey(key) && existingMetadata.TryGetValue(key, out string? value))
                metadata[key] = value;
        }
        return metadata.Count == 0 ? summary : summary with { Metadata = metadata };
    }

    private static (string? ApprovalMode, string? SandboxMode) NormalizeSecurityOptions(
        SecurityDescriptor security,
        string? approvalMode,
        string? sandboxMode) =>
        (
            NormalizeSecurityOption(security.ApprovalPolicies, approvalMode, "approval"),
            NormalizeSecurityOption(security.SandboxPolicies, sandboxMode, "sandbox")
        );

    private static string? NormalizeSecurityOption(
        IReadOnlyList<KernelSecurityPolicyOption>? supported,
        string? value,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        KernelSecurityPolicyOption? match = supported?.FirstOrDefault(option =>
            string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new NotSupportedException($"The selected kernel does not support the requested {kind} policy.");
        return match.Value;
    }

    private static KernelProfileKey Key(KernelProfile profile) => new(profile.AdapterId, profile.ProfileId);

    private static KernelProfileKey Key(SessionRef session) => new(session.AdapterId, session.ProfileId);

    private static void ValidateOwnership(KernelProfileKey key, SessionRef session)
    {
        if (!string.Equals(key.AdapterId, session.AdapterId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(key.ProfileId, session.ProfileId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The adapter returned a session owned by a different kernel profile.");
    }

    private static void ValidateProfile(KernelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.AdapterId) || string.IsNullOrWhiteSpace(profile.ProfileId))
            throw new ArgumentException("Kernel profiles require adapter and profile identities.", nameof(profile));
        if (!Path.IsPathFullyQualified(profile.DataRoot)) throw new ArgumentException("Kernel profile data roots must be absolute.", nameof(profile));
        foreach (KeyValuePair<string, string> pair in profile.Environment)
        {
            if (LooksSensitive(pair.Key))
                throw new ArgumentException($"Sensitive environment entry '{pair.Key}' is forbidden; configure credentials through the protected credential flow.", nameof(profile));
        }
    }

    private static bool LooksSensitive(string name) =>
        name.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase);

    private static bool CanPumpEvents(KernelAvailability availability) =>
        availability is KernelAvailability.Starting or KernelAvailability.Ready or KernelAvailability.Degraded;

    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("Initialize the kernel host before using it.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class HostedKernel(KernelProfile profile, IKernelAdapter adapter, CancellationTokenSource cancellation)
    {
        public KernelProfile Profile { get; } = profile;
        public IKernelAdapter Adapter { get; } = adapter;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? EventPump { get; set; }
        public string? EventPumpFailure { get; set; }
    }
}
