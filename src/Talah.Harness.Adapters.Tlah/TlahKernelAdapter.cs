using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Runtime;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace Talah.Harness.Adapters.Tlah;

public sealed class TlahKernelAdapter(ITlahNativeRuntime runtime, TimeSpan? shutdownTimeout = null) : IKernelAdapter, ISessionRenameAdapter
{
    public const string Id = "tlah";
    private const string NativeVersion = "4.16.0";
    private readonly ITlahNativeRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly TimeSpan _shutdownTimeout = ValidateShutdownTimeout(shutdownTimeout);
    private readonly PrioritizedEventBuffer<KernelEvent> _events = new(
        4_096,
        1_024,
        static item => item.Kind == KernelEventKind.ContentDelta);
    private readonly CancellationTokenSource _eventLifetime = new();
    private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _workspaceRoots = new();
    private KernelInitializationContext? _context;
    private long _sequence;
    private long _reportedDroppedDeltas;
    private bool _disposed;

    public string AdapterId => Id;

    public KernelDescriptor Descriptor => new(
        Id,
        "TLAH Studio",
        "native-dotnet",
        "1.0.0",
        NativeVersion,
        _context is null ? KernelAvailability.Stopped : KernelAvailability.Ready,
        new KernelCapabilities(
            CanAuthenticate: true,
            CanUseApiKey: true,
            CanListSessions: true,
            CanResumeSessions: true,
            CanForkSessions: false,
            CanArchiveSessions: true,
            CanSteerActiveTurn: false,
            CanCancelTurn: true,
            CanApproveTools: true,
            CanAmendToolInput: true,
            CanReadHistory: true,
            CanReturnDiffs: false,
            CanConfigureProviders: true,
            CanConfigureMcp: false,
            CanUseSubagents: true,
            CanReplayEvents: false),
        new SecurityDescriptor(
            SecurityEnforcementKind.PermissionGate,
            "TLAH Studio native tool authorization policy",
            _context is null ? [] : _workspaceRoots.Values.Append(_context.Profile.DataRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            NetworkRestricted: true,
            ProcessRestricted: true,
            IsVerifiedByHost: false,
            "TLAH validates tool effects and gates sensitive actions. This is not an OS sandbox; provider traffic and allowlisted tool traffic can leave the machine.",
            ApprovalPolicies:
            [
                new(AgentPermissionModes.RequestApproval, "Ask approval", "TLAH asks before tools that require an explicit decision."),
                new(AgentPermissionModes.Plan, "Plan mode", "Safe reads proceed; write or destructive tools require an explicit decision."),
                new(AgentPermissionModes.AutoApprove, "Auto approve", "TLAH automatically allows ordinary tools while retaining immutable safety blocks.", IsDangerous: true),
                new(AgentPermissionModes.BypassPermissions, "Danger: full access", "TLAH bypasses ordinary permission, path, network, and policy guards; immutable safety blocks remain.", IsDangerous: true)
            ],
            SandboxPolicies: null,
            DefaultApprovalPolicy: AgentPermissionModes.RequestApproval,
            DefaultSandboxPolicy: null),
        new Dictionary<string, string>
        {
            ["upstream"] = "TLAH-Studio",
            ["upstreamCommit"] = "3ff42e06dca0359ce499c490cc7879934d439150"
        });

    public async ValueTask InitializeAsync(KernelInitializationContext context, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_context is not null)
            throw new InvalidOperationException("This adapter instance is already initialized.");
        if (!string.Equals(context.Profile.AdapterId, Id, StringComparison.Ordinal))
            throw new ArgumentException($"Profile adapter id must be '{Id}'.", nameof(context));
        string dataRoot = NormalizeRoot(context.Profile.DataRoot, nameof(context));
        string profileRoot = Path.Combine(dataRoot, "profiles", SafeSegment(context.Profile.ProfileId));
        Directory.CreateDirectory(profileRoot);
        _context = context with { Profile = context.Profile with { DataRoot = profileRoot } };
        await _runtime.InitializeAsync(profileRoot, cancellationToken).ConfigureAwait(false);
        Publish(null, null, null, KernelEventKind.AdapterStatusChanged,
            new StatusEventData(KernelAvailability.Ready, "Native TLAH Core/Data runtime is ready."));
    }

    public async Task<KernelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is null)
            return new KernelHealth(KernelAvailability.Stopped, "Adapter has not been initialized.", DateTimeOffset.UtcNow, []);
        try
        {
            _ = await _runtime.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
            return new KernelHealth(KernelAvailability.Ready, "Native TLAH Core/Data graph and profile database are available.", DateTimeOffset.UtcNow, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new KernelHealth(KernelAvailability.Failed, "Native TLAH runtime health check failed.", DateTimeOffset.UtcNow,
                [new KernelDiagnostic("TLAH_NATIVE_HEALTH", DiagnosticSeverity.Error, Redact(ex.Message))]);
        }
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        bool configured = await _runtime.IsConfiguredAsync(cancellationToken).ConfigureAwait(false);
        GlobalSettingsDto settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new AuthenticationState(
            configured ? AuthenticationStatus.SignedIn : AuthenticationStatus.SignedOut,
            configured ? settings.Provider : null,
            null,
            [AuthenticationMethod.ApiKey],
            configured ? "A DPAPI-protected provider API key is configured." : "Configure a provider API key.");
    }

    public Task<LoginChallenge> BeginLoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("TLAH Studio supports provider API keys; it does not expose subscription, browser, or device-code login.");
    }

    public Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("TLAH Studio has no interactive login flow to cancel.");
    }

    public async Task ConfigureApiKeyAsync(ApiKeyCredential credential, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(credential.Secret))
            throw new ArgumentException("An API key is required.", nameof(credential));
        ApiKeyEndpointPolicy.Validate(credential.BaseUri, nameof(credential));
        IReadOnlyList<ProviderInfo> providers = await _runtime.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (!providers.Any(p => string.Equals(p.Key, credential.ProviderId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The requested provider is not supported by native TLAH.", nameof(credential));
        try
        {
            await _runtime.ConfigureAsync(credential.ProviderId, credential.Secret, credential.BaseUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Redact(ex.Message));
        }
        Publish(null, null, null, KernelEventKind.AuthenticationChanged,
            new StatusEventData(KernelAvailability.Ready, "Provider API key configured in the protected native settings store."));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _runtime.ClearCredentialAsync(cancellationToken).ConfigureAwait(false);
        Publish(null, null, null, KernelEventKind.AuthenticationChanged,
            new StatusEventData(KernelAvailability.Ready, "Provider credential removed."));
    }

    public async Task<IReadOnlyList<KernelModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        GlobalSettingsDto settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> models = await _runtime.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(model => new KernelModel(model, model, $"Native {settings.Provider} model", model == settings.Model,
            new Dictionary<string, string> { ["provider"] = settings.Provider })).ToArray();
    }

    public async Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        int offset = ParseCursor(request.Cursor);
        int size = Math.Clamp(request.PageSize, 1, 200);
        IReadOnlyList<ChatSummaryDto> chats = await _runtime.ListChatsAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
        KernelSessionSummary[] page = chats.Skip(offset).Take(size).Select(MapSession).ToArray();
        int next = offset + page.Length;
        return new ResultPage<KernelSessionSummary>(page, next < chats.Count ? next.ToString() : null, next < chats.Count);
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        string root = ValidateWorkspace(request.Workspace);
        Chat chat = await _runtime.CreateChatAsync(string.IsNullOrWhiteSpace(request.Title) ? "New Chat" : request.Title.Trim(), root, cancellationToken).ConfigureAwait(false);
        _workspaceRoots[chat.Id] = root;
        if (!string.IsNullOrWhiteSpace(request.ModelId))
            await _runtime.SetModelAsync(chat.Id, request.ModelId, cancellationToken).ConfigureAwait(false);
        KernelSessionSummary summary = MapSession(chat, request.Workspace.WorkspaceId);
        Publish(chat.Id, null, null, KernelEventKind.SessionCreated, new SessionEventData(summary));
        return summary;
    }

    public async Task<KernelSessionSummary> ResumeSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        Guid id = ValidateSession(session);
        Chat chat = await _runtime.GetChatAsync(id, cancellationToken).ConfigureAwait(false);
        AgentRunSnapshot? latestRun = await _runtime.GetLatestRunAsync(id, cancellationToken).ConfigureAwait(false);
        if (latestRun?.ChatId != id)
            latestRun = null;
        else
            RecoverPendingApproval(session, latestRun);
        return MapSession(chat, session.WorkspaceId, MapSessionStatus(chat, latestRun));
    }

    public Task<KernelSessionSummary> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The pinned native TLAH runtime does not expose atomic session forking.");
    }

    public async Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        Guid id = ValidateSession(session);
        Chat chat = await _runtime.SetArchivedAsync(id, true, cancellationToken).ConfigureAwait(false);
        Publish(id, null, null, KernelEventKind.SessionMetadataChanged, new SessionEventData(MapSession(chat)));
    }

    public async Task<KernelSessionSummary> RenameSessionAsync(
        SessionRef session,
        string title,
        CancellationToken cancellationToken = default)
    {
        Guid id = ValidateSession(session);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A session title is required.", nameof(title));
        Chat chat = await _runtime.RenameAsync(id, title.Trim(), cancellationToken).ConfigureAwait(false);
        KernelSessionSummary summary = MapSession(chat, session.WorkspaceId);
        Publish(id, null, null, KernelEventKind.SessionMetadataChanged, new SessionEventData(summary));
        return summary;
    }

    public async Task<KernelTurn> StartTurnAsync(SessionRef session, TurnInput input, TurnOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = NormalizePermissionMode(options);
        Guid chatId = ValidateSession(session);
        string prompt = FlattenInput(input);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("TLAH requires non-empty text input.", nameof(input));
        if (!string.IsNullOrWhiteSpace(options.ModelId))
            await _runtime.SetModelAsync(chatId, options.ModelId, cancellationToken).ConfigureAwait(false);

        string turnId = Guid.NewGuid().ToString("D");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_eventLifetime.Token);
        var active = new ActiveTurn(session, turnId, cts);
        if (!_activeTurns.TryAdd(turnId, active))
            throw new InvalidOperationException("Unable to allocate the turn.");
        Publish(chatId, turnId, null, KernelEventKind.TurnStarted, new TurnEventData(TurnStatus.Running));
        active.Execution = ExecuteTurnAsync(active, chatId, prompt, options);
        return new KernelTurn(session, turnId, TurnStatus.Running, DateTimeOffset.UtcNow);
    }

    public Task SteerTurnAsync(SessionRef session, string nativeTurnId, TurnInput input, CancellationToken cancellationToken = default)
    {
        _ = ValidateSession(session);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The pinned native TLAH agent cannot accept steering while a provider call is active.");
    }

    public async Task CancelTurnAsync(SessionRef session, string nativeTurnId, CancellationToken cancellationToken = default)
    {
        _ = ValidateSession(session);
        if (!_activeTurns.TryGetValue(nativeTurnId, out ActiveTurn? active))
            throw new InvalidOperationException("The requested turn is not active.");
        active.Cancellation.Cancel();
        if (active.RunId is Guid runId)
            await _runtime.CancelRunAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToPermissionAsync(PermissionResponse response, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!_approvals.TryRemove(response.PermissionId, out PendingApproval? pending))
            throw new InvalidOperationException("The permission request is unknown or has already been answered.");
        bool approved = response.ChoiceId is "allow-once" or "allow-session" or "allow-amended";
        string? amended = response.ChoiceId == "allow-amended" ? response.AmendedInput?.GetRawText() : null;
        await _runtime.SetApprovalAsync(pending.InvocationId, approved,
            response.ChoiceId == "allow-session" ? "chat" : "once", amended, cancellationToken).ConfigureAwait(false);
        ActiveTurn active = _activeTurns.GetValueOrDefault(pending.TurnId)
            ?? throw new InvalidOperationException("The turn associated with this approval is no longer active.");
        active.Execution = ResumeTurnAsync(active, pending.RunId);
    }

    public Task RespondToElicitationAsync(ElicitationResponse response, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The native ask-user tool is approval-gated but does not expose a durable schema response API through this adapter contract.");
    }

    public async Task<ResultPage<KernelItem>> ReadHistoryAsync(SessionRef session, PageRequest request, CancellationToken cancellationToken = default)
    {
        Guid chatId = ValidateSession(session);
        int offset = ParseCursor(request.Cursor);
        int size = Math.Clamp(request.PageSize, 1, 200);
        IReadOnlyList<Message> messages = await _runtime.ReadMessagesAsync(chatId, cancellationToken).ConfigureAwait(false);
        KernelItem[] page = messages.Skip(offset).Take(size).Select(MapMessage).ToArray();
        int next = offset + page.Length;
        return new ResultPage<KernelItem>(page, next < messages.Count ? next.ToString() : null, next < messages.Count);
    }

    public Task<KernelDiff?> ReadDiffAsync(SessionRef session, string? nativeTurnOrItemId = null, CancellationToken cancellationToken = default)
    {
        _ = ValidateSession(session);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<KernelDiff?>(null);
    }

    public async IAsyncEnumerable<KernelEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await foreach (KernelEvent? item in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            KernelEvent? gap = CreateDeltaGapEvent(item);
            if (gap is not null) yield return gap;
            yield return item;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _eventLifetime.Cancel();
        foreach (ActiveTurn turn in _activeTurns.Values)
            turn.Cancellation.Cancel();
        Task[] tasks = _activeTurns.Values.Select(turn => turn.Execution).Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(_shutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        catch { }
        foreach (ActiveTurn turn in _activeTurns.Values)
        {
            if (turn.Execution?.IsCompleted != false)
                turn.Cancellation.Dispose();
        }
        _activeTurns.Clear();
        _approvals.Clear();
        _events.Complete();
        _eventLifetime.Dispose();
        try { await _runtime.DisposeAsync().AsTask().WaitAsync(_shutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private async Task ExecuteTurnAsync(ActiveTurn active, Guid chatId, string prompt, TurnOptions options)
    {
        var progress = new InlineProgress<AgentProgressUpdate>(update => HandleProgress(active, update));
        var output = new InlineProgress<LlmStreamUpdate>(update =>
        {
            if (!string.IsNullOrEmpty(update.Delta))
                Publish(chatId, active.TurnId, null, KernelEventKind.ContentDelta,
                    new ContentDeltaEventData(update.EventType == LlmStreamEventTypes.ThinkingDelta ? "reasoning" : "assistant", update.Delta, "markdown"));
        });
        try
        {
            SendMessageResult result = await _runtime.RunAsync(chatId, prompt, BuildOptions(options, output, progress), active.Cancellation.Token).ConfigureAwait(false);
            CompleteFromResult(active, result);
        }
        catch (OperationCanceledException)
        {
            Publish(chatId, active.TurnId, null, KernelEventKind.TurnCancelled, new TurnEventData(TurnStatus.Cancelled));
            Finish(active);
        }
        catch (Exception ex)
        {
            Publish(chatId, active.TurnId, null, KernelEventKind.TurnFailed,
                new DiagnosticEventData(new KernelDiagnostic("TLAH_NATIVE_TURN", DiagnosticSeverity.Error, Redact(ex.Message))));
            Finish(active);
        }
    }

    private async Task ResumeTurnAsync(ActiveTurn active, Guid runId)
    {
        try
        {
            var progress = new InlineProgress<AgentProgressUpdate>(update => HandleProgress(active, update));
            var output = new InlineProgress<LlmStreamUpdate>(update =>
            {
                if (!string.IsNullOrEmpty(update.Delta))
                    Publish(active.SessionId, active.TurnId, null, KernelEventKind.ContentDelta,
                        new ContentDeltaEventData(update.EventType == LlmStreamEventTypes.ThinkingDelta ? "reasoning" : "assistant", update.Delta, "markdown"));
            });
            SendMessageResult result = await _runtime.ResumeRunAsync(runId, BuildOptions(new TurnOptions(null, null, null, null), output, progress), active.Cancellation.Token).ConfigureAwait(false);
            CompleteFromResult(active, result);
        }
        catch (OperationCanceledException)
        {
            Publish(active.SessionId, active.TurnId, null, KernelEventKind.TurnCancelled, new TurnEventData(TurnStatus.Cancelled));
            Finish(active);
        }
        catch (Exception ex)
        {
            Publish(active.SessionId, active.TurnId, null, KernelEventKind.TurnFailed,
                new DiagnosticEventData(new KernelDiagnostic("TLAH_NATIVE_RESUME", DiagnosticSeverity.Error, Redact(ex.Message))));
            Finish(active);
        }
    }

    private static AgentRunOptions BuildOptions(TurnOptions options, IProgress<LlmStreamUpdate> output, IProgress<AgentProgressUpdate> progress)
    {
        string permissionMode = NormalizePermissionMode(options);
        return new AgentRunOptions(
            AutoApproveTools: permissionMode is AgentPermissionModes.AutoApprove or AgentPermissionModes.BypassPermissions,
            OutputStream: output,
            Progress: progress,
            PermissionMode: permissionMode);
    }

    private static string NormalizePermissionMode(TurnOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SandboxMode))
            throw new NotSupportedException("Native TLAH exposes a tool-authorization policy, not a selectable OS sandbox.");
        return options.ApprovalMode?.Trim().ToLowerInvariant() switch
        {
            null or "" => AgentPermissionModes.RequestApproval,
            AgentPermissionModes.RequestApproval => AgentPermissionModes.RequestApproval,
            AgentPermissionModes.Plan => AgentPermissionModes.Plan,
            AgentPermissionModes.AutoApprove => AgentPermissionModes.AutoApprove,
            AgentPermissionModes.BypassPermissions => AgentPermissionModes.BypassPermissions,
            _ => throw new NotSupportedException($"Native TLAH permission mode '{options.ApprovalMode}' is not supported.")
        };
    }

    private void HandleProgress(ActiveTurn active, AgentProgressUpdate update)
    {
        active.RunId = update.AgentRunId;
        if (update.EventType == AgentEventTypes.ApprovalRequested && update.ToolInvocationId is Guid invocation)
        {
            JsonElement data = ParseJson(update.DataJson);
            PermissionRequest? request = RegisterApproval(
                active,
                update.AgentRunId,
                invocation,
                TryString(data, "toolName") ?? "native-tool",
                TryObject(data, "arguments") ?? "{}",
                TryString(data, "safetyLevel") ?? "unknown",
                update.Summary,
                data);
            if (request is not null)
                Publish(active.SessionId, active.TurnId, invocation, KernelEventKind.PermissionRequested, new PermissionEventData(request));
            return;
        }

        KernelEventKind kind = update.EventType switch
        {
            AgentEventTypes.ToolRequest or AgentEventTypes.ToolStarted => KernelEventKind.ItemStarted,
            AgentEventTypes.ToolProgress => KernelEventKind.ItemUpdated,
            AgentEventTypes.ToolResult => KernelEventKind.ItemCompleted,
            AgentEventTypes.Error => KernelEventKind.Diagnostic,
            _ => KernelEventKind.Diagnostic
        };
        if (kind is KernelEventKind.ItemStarted or KernelEventKind.ItemUpdated or KernelEventKind.ItemCompleted)
        {
            var item = new KernelItem(
                update.ToolInvocationId?.ToString("D") ?? update.AgentStepId?.ToString("D") ?? $"event-{update.SequenceNumber}",
                update.EventType == AgentEventTypes.ToolResult ? KernelItemKind.ToolResult : KernelItemKind.ToolCall,
                kind == KernelEventKind.ItemCompleted ? KernelItemStatus.Completed : KernelItemStatus.Running,
                update.Summary,
                [new NoticeContentBlock(update.EventType, update.Summary, MapSeverity(update.Severity))],
                new DateTimeOffset(update.CreatedAt, TimeSpan.Zero),
                new DateTimeOffset(update.CreatedAt, TimeSpan.Zero),
                VendorData: ParseJson(update.DataJson));
            Publish(active.SessionId, active.TurnId, update.ToolInvocationId, kind, new ItemEventData(item));
        }
        else
        {
            Publish(active.SessionId, active.TurnId, update.ToolInvocationId, kind,
                new DiagnosticEventData(new KernelDiagnostic($"TLAH_{update.EventType.ToUpperInvariant()}", MapSeverity(update.Severity), Redact(update.Summary), VendorData: ParseJson(update.DataJson))));
        }
    }

    private void CompleteFromResult(ActiveTurn active, SendMessageResult result)
    {
        active.RunId = result.AgentRun?.Id;
        if (!string.IsNullOrWhiteSpace(result.RawResponse.TokenUsageJson))
        {
            JsonElement usage = ParseJson(result.RawResponse.TokenUsageJson);
            Publish(active.SessionId, active.TurnId, null, KernelEventKind.UsageUpdated,
                new UsageEventData(
                    TryLong(usage, "prompt_tokens") ?? TryLong(usage, "input_tokens"),
                    TryLong(usage, "completion_tokens") ?? TryLong(usage, "output_tokens"),
                    TryLong(usage, "cached_tokens") ?? TryLong(usage, "cached_input_tokens"),
                    null,
                    null));
        }
        string status = result.AgentRun?.Status ?? AgentRunStatuses.Completed;
        if (status == AgentRunStatuses.AwaitingApproval)
            return;
        KernelEventKind kind = status switch
        {
            AgentRunStatuses.Cancelled => KernelEventKind.TurnCancelled,
            AgentRunStatuses.Failed => KernelEventKind.TurnFailed,
            _ => KernelEventKind.TurnCompleted
        };
        TurnStatus turnStatus = status switch
        {
            AgentRunStatuses.Cancelled => TurnStatus.Cancelled,
            AgentRunStatuses.Failed => TurnStatus.Failed,
            _ => TurnStatus.Completed
        };
        Publish(active.SessionId, active.TurnId, null, kind, new TurnEventData(turnStatus, Redact(result.AgentRun?.ErrorMessage)));
        Finish(active);
    }

    private void Finish(ActiveTurn active)
    {
        _activeTurns.TryRemove(active.TurnId, out _);
        active.Cancellation.Dispose();
    }

    private void RecoverPendingApproval(SessionRef session, AgentRunSnapshot run)
    {
        ToolInvocationSnapshot? invocation = run.Status == AgentRunStatuses.AwaitingApproval
            ? run.PendingApproval
            : null;
        if (invocation is null || invocation.Status != ToolInvocationStatuses.AwaitingApproval)
            return;

        string turnId = run.TurnId.ToString("D");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_eventLifetime.Token);
        var active = new ActiveTurn(session, turnId, cancellation) { RunId = run.Id };
        if (!_activeTurns.TryAdd(turnId, active))
        {
            cancellation.Dispose();
            return;
        }

        JsonElement vendorData = JsonSerializer.SerializeToElement(new
        {
            toolName = invocation.ToolName,
            arguments = ParseJson(invocation.ArgumentsJson),
            safetyLevel = invocation.SafetyLevel,
            safetySummary = invocation.SafetySummary,
            safety = ParseJson(invocation.SafetyJson),
            safetyWarning = invocation.SafetyWarning,
            recoveredFromCheckpoint = true
        });
        string reason = string.IsNullOrWhiteSpace(invocation.SafetySummary)
            ? "A native tool checkpoint is waiting for approval."
            : invocation.SafetySummary;

        PermissionRequest? request = RegisterApproval(
                active,
                run.Id,
                invocation.Id,
                invocation.ToolName,
                invocation.ArgumentsJson,
                invocation.SafetyLevel,
                reason,
                vendorData);
        if (request is null)
        {
            _activeTurns.TryRemove(turnId, out _);
            cancellation.Dispose();
            return;
        }

        Publish(active.SessionId, active.TurnId, null, KernelEventKind.TurnStarted,
            new TurnEventData(TurnStatus.WaitingForApproval, "Recovered a native run waiting for approval."));
        Publish(active.SessionId, active.TurnId, invocation.Id, KernelEventKind.PermissionRequested, new PermissionEventData(request));
    }

    private PermissionRequest? RegisterApproval(
        ActiveTurn active,
        Guid runId,
        Guid invocationId,
        string toolName,
        string arguments,
        string safetyLevel,
        string reason,
        JsonElement vendorData)
    {
        string permissionId = invocationId.ToString("D");
        if (!_approvals.TryAdd(permissionId, new PendingApproval(invocationId, runId, active.TurnId)))
            return null;

        return new PermissionRequest(
            permissionId,
            active.Session,
            active.TurnId,
            "tlah-tool",
            $"Allow {toolName}?",
            reason,
            [new ResourceImpact("tool", toolName, "execute", safetyLevel, arguments)],
            [
                new PermissionChoice("deny", PermissionDecisionKind.Deny, "Deny", "Do not run this tool."),
                new PermissionChoice("allow-once", PermissionDecisionKind.AllowOnce, "Allow once", "Allow this invocation."),
                new PermissionChoice("allow-session", PermissionDecisionKind.AllowForSession, "Allow for session", "Remember native TLAH policy for this chat."),
                new PermissionChoice("allow-amended", PermissionDecisionKind.AllowWithAmendedInput, "Allow amended", "Run with amended JSON input.", true)
            ],
            vendorData);
    }

    private void Publish(Guid? sessionId, string? turnId, Guid? itemId, KernelEventKind kind, KernelEventData data)
    {
        KernelInitializationContext? context = _context;
        if (context is null || _disposed)
            return;
        try
        {
            _events.TryWrite(new KernelEvent(
                Id,
                context.Profile.ProfileId,
                sessionId?.ToString("D"),
                turnId,
                itemId?.ToString("D"),
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow,
                kind,
                data));
        }
        catch (OperationCanceledException) when (_eventLifetime.IsCancellationRequested)
        {
        }
    }

    private KernelEvent? CreateDeltaGapEvent(KernelEvent next)
    {
        long dropped = _events.DroppedCount;
        long gap = dropped - Interlocked.Read(ref _reportedDroppedDeltas);
        if (gap <= 0) return null;
        Interlocked.Exchange(ref _reportedDroppedDeltas, dropped);
        return new KernelEvent(
            Id,
            EnsureInitialized().Profile.ProfileId,
            next.NativeSessionId,
            next.NativeTurnId,
            null,
            next.Sequence,
            next.Timestamp,
            KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic(
                "TLAH_EVENT_DELTA_GAP",
                DiagnosticSeverity.Warning,
                $"Dropped {gap} streaming content delta event(s) under sustained backpressure; refresh native history to reconcile complete content.")));
    }

    private KernelSessionSummary MapSession(ChatSummaryDto chat) => new(
        MakeSession(chat.Id),
        chat.Title,
        chat.IsArchived ? SessionStatus.Archived : SessionStatus.Idle,
        new DateTimeOffset(chat.UpdatedAt, TimeSpan.Zero),
        new DateTimeOffset(chat.UpdatedAt, TimeSpan.Zero),
        chat.MessageCount == 0 ? null : $"{chat.MessageCount} messages",
        new Dictionary<string, string> { ["messageCount"] = chat.MessageCount.ToString() });

    private KernelSessionSummary MapSession(Chat chat, string? workspaceId = null, SessionStatus? status = null) => new(
        MakeSession(chat.Id, workspaceId),
        chat.Title,
        chat.IsArchived ? SessionStatus.Archived : status ?? SessionStatus.Idle,
        new DateTimeOffset(chat.CreatedAt, TimeSpan.Zero),
        new DateTimeOffset(chat.UpdatedAt, TimeSpan.Zero),
        null);

    private static SessionStatus MapSessionStatus(Chat chat, AgentRunSnapshot? run)
    {
        if (chat.IsArchived)
            return SessionStatus.Archived;
        return run?.Status switch
        {
            AgentRunStatuses.Running => SessionStatus.Running,
            AgentRunStatuses.AwaitingApproval => SessionStatus.WaitingForApproval,
            AgentRunStatuses.Paused => SessionStatus.Paused,
            AgentRunStatuses.Completed => SessionStatus.Completed,
            AgentRunStatuses.Failed => SessionStatus.Failed,
            _ => SessionStatus.Idle
        };
    }

    private SessionRef MakeSession(Guid chatId, string? workspaceId = null) =>
        new(Id, EnsureInitialized().Profile.ProfileId, chatId.ToString("D"), WorkspaceId: workspaceId);

    private static KernelItem MapMessage(Message message)
    {
        var created = new DateTimeOffset(message.CreatedAt, TimeSpan.Zero);
        return new KernelItem(
            message.Id.ToString("D"),
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? KernelItemKind.AssistantMessage : KernelItemKind.UserMessage,
            KernelItemStatus.Completed,
            null,
            [new TextContentBlock(message.Content)],
            created,
            created,
            message.TurnId?.ToString("D"));
    }

    private static string ValidateWorkspace(WorkspaceDescriptor workspace)
    {
        if (!workspace.IsTrusted)
            throw new UnauthorizedAccessException("TLAH native tools require an explicitly trusted workspace.");
        string root = NormalizeRoot(workspace.RootPath, nameof(workspace));
        foreach (string additional in workspace.AdditionalRoots)
        {
            string normalized = NormalizeRoot(additional, nameof(workspace));
            if (!IsContained(root, normalized))
                throw new UnauthorizedAccessException("Additional workspace roots must be contained by the primary root.");
        }
        return root;
    }

    private Guid ValidateSession(SessionRef session)
    {
        KernelInitializationContext context = EnsureInitialized();
        if (!string.Equals(session.AdapterId, Id, StringComparison.Ordinal) ||
            !string.Equals(session.ProfileId, context.Profile.ProfileId, StringComparison.Ordinal))
            throw new ArgumentException("The session belongs to another adapter or profile.", nameof(session));
        if (!Guid.TryParse(session.NativeSessionId, out Guid id))
            throw new ArgumentException("Native TLAH session ids are GUIDs.", nameof(session));
        return id;
    }

    private KernelInitializationContext EnsureInitialized()
    {
        ThrowIfDisposed();
        return _context ?? throw new InvalidOperationException("The adapter is not initialized.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string FlattenInput(TurnInput input) => string.Join("\n\n", input.Content.Select(block => block switch
    {
        TextContentBlock text => text.Text,
        NoticeContentBlock notice => $"{notice.Title}: {notice.Message}",
        ImageContentBlock image => $"[Image: {image.Uri}]",
        _ => throw new NotSupportedException($"TLAH prompt input does not support {block.GetType().Name}.")
    }));

    private static int ParseCursor(string? cursor) =>
        cursor is null ? 0 : int.TryParse(cursor, out int value) && value >= 0
            ? value
            : throw new ArgumentException("Cursor is invalid.", nameof(cursor));

    private static string NormalizeRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A root path is required.", parameterName);
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(full))
            throw new ArgumentException("Root paths must be absolute.", parameterName);
        return full;
    }

    private static bool IsContained(string root, string candidate) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value is "." or "..")
            throw new ArgumentException("Profile id is not a safe path segment.", nameof(value));
        return value;
    }

    private static TimeSpan ValidateShutdownTimeout(TimeSpan? value)
    {
        TimeSpan timeout = value ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(value), "Shutdown timeout must be finite and positive.");
        return timeout;
    }

    private static string Redact(string? message) => TLAHStudio.Core.Helpers.SecretRedactor.RedactText(message ?? string.Empty);

    private static JsonElement ParseJson(string json)
    {
        const int maximumBytes = 1024 * 1024;
        if (json.Length > maximumBytes || Encoding.UTF8.GetByteCount(json) > maximumBytes)
            return JsonSerializer.SerializeToElement(new { nativeDataOmitted = true, characterCount = json.Length, maximumBytes });
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { malformedNativeData = true }); }
    }

    private static string? TryString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property)
            ? property.GetRawText()
            : null;

    private static long? TryLong(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result)
            ? result
            : null;

    private static DiagnosticSeverity MapSeverity(string severity) => severity switch
    {
        AgentEventSeverities.Error => DiagnosticSeverity.Error,
        AgentEventSeverities.Warning => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Information
    };

    private sealed class ActiveTurn(SessionRef session, string turnId, CancellationTokenSource cancellation)
    {
        public SessionRef Session { get; } = session;
        public Guid SessionId { get; } = Guid.Parse(session.NativeSessionId);
        public string TurnId { get; } = turnId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Guid? RunId { get; set; }
        public Task? Execution { get; set; }
    }

    private sealed record PendingApproval(Guid InvocationId, Guid RunId, string TurnId);
}
