using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Runtime;

namespace Talah.Harness.Adapters.Codex;

public sealed class CodexKernelAdapter : IKernelAdapter, ISessionRenameAdapter
{
    public const string CodexAdapterId = "codex";
    private readonly KernelProfile _profile;
    private readonly CodexAppServerClient _client;
    private readonly PrioritizedEventBuffer<KernelEvent> _events = new(
        4_096,
        1_024,
        static item => item.Kind == KernelEventKind.ContentDelta);
    private readonly ConcurrentDictionary<string, PendingInteraction> _interactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _diffs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _writableRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private long _sequence;
    private long _reportedDroppedDeltas;
    private int _initialized;
    private int _disposed;
    private string? _nativeVersion;
    private KernelAvailability _availability = KernelAvailability.Starting;

    internal CodexKernelAdapter(KernelProfile profile, CodexAppServerClient client)
    {
        _profile = profile;
        _client = client;
        _client.NotificationReceived += OnNotificationAsync;
        _client.ServerRequestReceived += OnServerRequestAsync;
        _client.DiagnosticReceived += OnDiagnostic;
    }

    public string AdapterId => CodexAdapterId;

    public KernelDescriptor Descriptor => new(
        AdapterId,
        "OpenAI Codex",
        "codex-app-server-stdio",
        "1.0.0",
        _nativeVersion,
        _availability,
        new KernelCapabilities(
            CanAuthenticate: true,
            CanUseApiKey: true,
            CanListSessions: true,
            CanResumeSessions: true,
            CanForkSessions: true,
            CanArchiveSessions: true,
            CanSteerActiveTurn: true,
            CanCancelTurn: true,
            CanApproveTools: true,
            CanAmendToolInput: false,
            CanReadHistory: true,
            CanReturnDiffs: true,
            CanConfigureProviders: false,
            CanConfigureMcp: false,
            CanUseSubagents: true,
            CanReplayEvents: false),
        new SecurityDescriptor(
            SecurityEnforcementKind.OperatingSystemSandbox,
            "Codex App Server",
            _writableRoots.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            NetworkRestricted: false,
            ProcessRestricted: false,
            IsVerifiedByHost: false,
            "Codex enforces the selected sandbox and approval policy; the host displays and correlates approvals.",
            ApprovalPolicies:
            [
                new("on-request", "Ask on request", "Codex asks when its policy determines user approval is needed."),
                new("untrusted", "Ask for untrusted actions", "Codex requests approval for actions it classifies as untrusted."),
                new("never", "Never request approval", "Codex will not ask for escalation; operations outside the active sandbox policy cannot be approved.")
            ],
            SandboxPolicies:
            [
                new("workspace-write", "Workspace write", "Codex may write within the selected workspace under its native sandbox."),
                new("read-only", "Read only", "Codex receives its native read-only sandbox policy."),
                new("danger-full-access", "Danger: full access", "Codex receives its native unrestricted sandbox policy.", IsDangerous: true)
            ],
            DefaultApprovalPolicy: "on-request",
            DefaultSandboxPolicy: "workspace-write"),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "0.147.0",
            ["transport"] = "newline-delimited-json"
        });

    public async ValueTask InitializeAsync(
        KernelInitializationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(context.Profile.ProfileId, _profile.ProfileId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Initialization profile does not match the adapter profile.", nameof(context));
        }

        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return;
        }

        try
        {
            JsonElement result = await _client.InitializeAsync(context.HostVersion, cancellationToken).ConfigureAwait(false);
            _nativeVersion = GetString(result, "userAgent") ?? "codex-cli 0.147.0";
            _availability = KernelAvailability.Ready;
            Emit(null, null, null, KernelEventKind.AdapterStatusChanged,
                new StatusEventData(_availability, "Codex App Server initialized."), result);
        }
        catch
        {
            _availability = KernelAvailability.Failed;
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    public Task<KernelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KernelDiagnostic[] diagnostics = _availability == KernelAvailability.Ready
            ? Array.Empty<KernelDiagnostic>()
            : [new KernelDiagnostic("CODEX_NOT_READY", DiagnosticSeverity.Warning, "Codex App Server is not ready.")];
        return Task.FromResult(new KernelHealth(
            _availability,
            _availability == KernelAvailability.Ready ? "Codex App Server is ready." : "Codex App Server is unavailable.",
            DateTimeOffset.UtcNow,
            diagnostics));
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        JsonElement result = await _client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.TryGetProperty("account", out JsonElement account) || account.ValueKind == JsonValueKind.Null)
        {
            return new AuthenticationState(
                AuthenticationStatus.SignedOut,
                null,
                null,
                SupportedAuthenticationMethods,
                GetBoolean(result, "requiresOpenaiAuth") == true ? "OpenAI authentication is required." : null);
        }

        string? type = GetString(account, "type");
        string? label = type == "chatgpt" ? GetString(account, "email") : type;
        return new AuthenticationState(
            AuthenticationStatus.SignedIn,
            label,
            GetString(account, "planType"),
            SupportedAuthenticationMethods);
    }

    public async Task<LoginChallenge> BeginLoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        object parameters = request.Method switch
        {
            AuthenticationMethod.Browser => new
            {
                type = "chatgpt",
                appBrand = "codex",
                useHostedLoginSuccessPage = true
            },
            AuthenticationMethod.DeviceCode => new { type = "chatgptDeviceCode" },
            AuthenticationMethod.ApiKey => throw new NotSupportedException(
                "Use ConfigureApiKeyAsync so the secret can be handled as a credential."),
            _ => throw new NotSupportedException($"Codex schema 0.147.0 does not expose login method '{request.Method}'.")
        };
        JsonElement result = await _client.RequestAsync("account/login/start", parameters, cancellationToken)
            .ConfigureAwait(false);
        string? type = GetString(result, "type");
        string loginId = GetString(result, "loginId") ?? throw new CodexProtocolException("Login response has no loginId.");
        string? uriText = type == "chatgpt" ? GetString(result, "authUrl") : GetString(result, "verificationUrl");
        Uri? uri = Uri.TryCreate(uriText, UriKind.Absolute, out Uri? parsedUri) ? parsedUri : null;
        return new LoginChallenge(
            loginId,
            request.Method,
            uri,
            GetString(result, "userCode"),
            null,
            request.Method == AuthenticationMethod.Browser
                ? "Open the official ChatGPT sign-in page and complete authentication."
                : "Open the verification page and enter the device code.");
    }

    public async Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _client.RequestAsync("account/login/cancel", new { loginId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureApiKeyAsync(ApiKeyCredential credential, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (!string.Equals(credential.ProviderId, "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Pinned Codex schema supports host API-key configuration only for provider 'openai'.");
        }

        if (credential.BaseUri is not null || credential.Options is { Count: > 0 })
        {
            throw new NotSupportedException("Custom provider/base-URI configuration is not exposed by this adapter.");
        }

        if (string.IsNullOrWhiteSpace(credential.Secret))
        {
            throw new ArgumentException("API key cannot be empty.", nameof(credential));
        }

        await _client.RequestAsync(
            "account/login/start",
            new { type = "apiKey", apiKey = credential.Secret },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _client.RequestAsync("account/logout", new { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KernelModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var models = new List<KernelModel>();
        string? cursor = null;
        do
        {
            JsonElement result = await _client.RequestAsync(
                "model/list",
                new { cursor, includeHidden = false, limit = 100 },
                cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement model in data.EnumerateArray())
                {
                    models.Add(new KernelModel(
                        GetString(model, "id") ?? GetString(model, "model") ?? "unknown",
                        GetString(model, "displayName") ?? GetString(model, "model") ?? "Unknown model",
                        GetString(model, "description"),
                        GetBoolean(model, "isDefault") == true,
                        new Dictionary<string, string>
                        {
                            ["nativeModel"] = GetString(model, "model") ?? string.Empty,
                            ["defaultReasoningEffort"] = GetString(model, "defaultReasoningEffort") ?? string.Empty
                        }));
                }
            }

            cursor = GetString(result, "nextCursor");
        }
        while (cursor is not null);

        return models;
    }

    public async Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ValidatePage(request);
        JsonElement result = await _client.RequestAsync(
            "thread/list",
            new { cursor = request.Cursor, limit = request.PageSize, archived = false, sortKey = "updated_at", sortDirection = "desc" },
            cancellationToken).ConfigureAwait(false);
        KernelSessionSummary[] items = GetArray(result, "data").Select(thread => MapSession(thread)).ToArray();
        string? next = GetString(result, "nextCursor");
        return new ResultPage<KernelSessionSummary>(items, next, next is not null);
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        JsonElement result = await _client.RequestAsync(
            "thread/start",
            new
            {
                cwd = request.Workspace.RootPath,
                runtimeWorkspaceRoots = request.Workspace.AdditionalRoots.Prepend(request.Workspace.RootPath).Distinct().ToArray(),
                model = request.ModelId,
                experimentalRawEvents = false
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement thread = RequiredProperty(result, "thread");
        KernelSessionSummary summary = MapSession(thread, request.Title);
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            await _client.RequestAsync("thread/name/set", new { threadId = summary.Session.NativeSessionId, name = request.Title }, cancellationToken)
                .ConfigureAwait(false);
            summary = summary with { Title = request.Title! };
        }

        TrackWorkspace(request.Workspace);

        return summary;
    }

    public async Task<KernelSessionSummary> ResumeSessionAsync(
        SessionRef session,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        JsonElement result = await _client.RequestAsync(
            "thread/resume",
            new { threadId = session.NativeSessionId },
            cancellationToken).ConfigureAwait(false);
        return MapSession(RequiredProperty(result, "thread"));
    }

    public async Task<KernelSessionSummary> ForkSessionAsync(
        ForkSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(request.Session);
        if (request.Point is not null && request.Point.Kind != ForkPointKind.Turn)
        {
            throw new ArgumentException(
                "Codex thread/fork accepts a native turn ID, not an item or message ID.",
                nameof(request));
        }

        string? nativeTurnId = ValidateForkPoint(request.Point);
        JsonElement result = await _client.RequestAsync(
            "thread/fork",
            new { threadId = request.Session.NativeSessionId, lastTurnId = nativeTurnId },
            cancellationToken).ConfigureAwait(false);
        KernelSessionSummary summary = MapSession(RequiredProperty(result, "thread"), request.Title);
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            await _client.RequestAsync("thread/name/set", new { threadId = summary.Session.NativeSessionId, name = request.Title }, cancellationToken)
                .ConfigureAwait(false);
            summary = summary with { Title = request.Title! };
        }

        return summary;
    }

    private static string? ValidateForkPoint(NativeForkPoint? point)
    {
        if (point is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(point.NativeId))
        {
            throw new ArgumentException("A native fork-point ID cannot be empty.", nameof(point));
        }

        return point.NativeId;
    }

    public async Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        await _client.RequestAsync("thread/archive", new { threadId = session.NativeSessionId }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<KernelSessionSummary> RenameSessionAsync(
        SessionRef session,
        string title,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A session title is required.", nameof(title));
        await _client.RequestAsync(
                "thread/name/set",
                new { threadId = session.NativeSessionId, name = title.Trim() },
                cancellationToken)
            .ConfigureAwait(false);
        return await ResumeSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KernelTurn> StartTurnAsync(
        SessionRef session,
        TurnInput input,
        TurnOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        JsonElement result = await _client.RequestAsync(
            "turn/start",
            new
            {
                threadId = session.NativeSessionId,
                input = MapInput(input),
                model = options.ModelId,
                approvalPolicy = NormalizeApprovalMode(options.ApprovalMode),
                sandboxPolicy = MapSandbox(options.SandboxMode)
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement turn = RequiredProperty(result, "turn");
        return MapTurn(session, turn);
    }

    public async Task SteerTurnAsync(
        SessionRef session,
        string nativeTurnId,
        TurnInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        await _client.RequestAsync(
            "turn/steer",
            new { threadId = session.NativeSessionId, expectedTurnId = nativeTurnId, input = MapInput(input) },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelTurnAsync(
        SessionRef session,
        string nativeTurnId,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        await _client.RequestAsync(
            "turn/interrupt",
            new { threadId = session.NativeSessionId, turnId = nativeTurnId },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToPermissionAsync(PermissionResponse response, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (!_interactions.TryRemove(response.PermissionId, out PendingInteraction? pending) || pending.Kind != InteractionKind.Permission)
        {
            throw new KeyNotFoundException($"No pending Codex permission '{response.PermissionId}'.");
        }

        if (response.AmendedInput is not null)
        {
            _interactions.TryAdd(response.PermissionId, pending);
            throw new NotSupportedException("Codex input amendment shapes are operation-specific and are not advertised by this adapter.");
        }

        if (pending.NativeKind == "permissions")
        {
            JsonElement requestedPermissions = pending.Parameters.TryGetProperty("permissions", out JsonElement permissions)
                ? permissions.Clone()
                : JsonSerializer.SerializeToElement(new { });
            JsonElement grantedPermissions = response.ChoiceId == "deny"
                ? JsonSerializer.SerializeToElement(new { })
                : requestedPermissions;
            string scope = response.ChoiceId switch
            {
                "allow-once" => "turn",
                "allow-session" => "session",
                "deny" => "turn",
                _ => throw new ArgumentException($"Unknown permission choice '{response.ChoiceId}'.", nameof(response))
            };
            await _client.RespondAsync(
                pending.RequestId,
                new { permissions = grantedPermissions, scope },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string decision = response.ChoiceId switch
        {
            "allow-once" => "accept",
            "allow-session" => "acceptForSession",
            "deny" => "decline",
            _ => throw new ArgumentException($"Unknown permission choice '{response.ChoiceId}'.", nameof(response))
        };
        await _client.RespondAsync(pending.RequestId, new { decision }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToElicitationAsync(ElicitationResponse response, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (!_interactions.TryRemove(response.RequestId, out PendingInteraction? pending) || pending.Kind == InteractionKind.Permission)
        {
            throw new KeyNotFoundException($"No pending Codex elicitation '{response.RequestId}'.");
        }

        object result;
        if (pending.Kind == InteractionKind.ToolInput)
        {
            if (response.Cancelled)
            {
                result = new { answers = new Dictionary<string, object>() };
            }
            else
            {
                Dictionary<string, object> answers = response.Value is { ValueKind: JsonValueKind.Object }
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.Value.Value.GetRawText())!
                        .ToDictionary(pair => pair.Key, pair => (object)new { answers = ToStringArray(pair.Value) })
                    : throw new ArgumentException("Tool input response must be an object keyed by question id.", nameof(response));
                result = new { answers };
            }
        }
        else
        {
            result = response.Cancelled
                ? new { action = "cancel", content = (object?)null }
                : new { action = "accept", content = response.Value };
        }

        await _client.RespondAsync(pending.RequestId, result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResultPage<KernelItem>> ReadHistoryAsync(
        SessionRef session,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        ValidatePage(request);
        JsonElement result = await _client.RequestAsync(
            "thread/items/list",
            new { threadId = session.NativeSessionId, cursor = request.Cursor, limit = request.PageSize, sortDirection = "asc" },
            cancellationToken).ConfigureAwait(false);
        KernelItem[] items = GetArray(result, "data").Select(item => MapItem(item, null)).ToArray();
        string? next = GetString(result, "nextCursor");
        return new ResultPage<KernelItem>(items, next, next is not null);
    }

    public async Task<KernelDiff?> ReadDiffAsync(
        SessionRef session,
        string? nativeTurnOrItemId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        string key = DiffKey(session.NativeSessionId, nativeTurnOrItemId);
        if (!_diffs.TryGetValue(key, out string? diff) && nativeTurnOrItemId is not null)
        {
            _diffs.TryGetValue(DiffKey(session.NativeSessionId, null), out diff);
        }

        if (diff is null)
        {
            JsonElement result = await _client.RequestAsync(
                "thread/read",
                new { threadId = session.NativeSessionId, includeTurns = true },
                cancellationToken)
                .ConfigureAwait(false);
            diff = ExtractThreadDiff(result, nativeTurnOrItemId);
            if (diff is null)
            {
                return null;
            }
        }

        return new KernelDiff(session, diff, ParseDiffPaths(diff), IsTruncated: false);
    }

    public async IAsyncEnumerable<KernelEvent> WatchEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await foreach (KernelEvent? item in _events.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            KernelEvent? gap = CreateDeltaGapEvent(item);
            if (gap is not null) yield return gap;
            yield return item;
        }
    }

    private Task OnNotificationAsync(string method, JsonElement parameters, JsonElement vendorData)
    {
        string? threadId = GetString(parameters, "threadId");
        string? turnId = GetString(parameters, "turnId");
        string? itemId = GetString(parameters, "itemId");
        switch (method)
        {
            case "account/updated":
            case "account/login/completed":
                Emit(threadId, turnId, itemId, KernelEventKind.AuthenticationChanged,
                    new StatusEventData(KernelAvailability.Ready, method == "account/updated" ? "Codex account changed." : "Codex login completed."), vendorData);
                break;
            case "thread/started":
                if (parameters.TryGetProperty("thread", out JsonElement startedThread))
                {
                    KernelSessionSummary summary = MapSession(startedThread);
                    Emit(summary.Session.NativeSessionId, null, null, KernelEventKind.SessionCreated, new SessionEventData(summary), vendorData);
                }

                break;
            case "thread/status/changed":
            case "thread/name/updated":
                Emit(threadId, turnId, itemId, KernelEventKind.SessionMetadataChanged,
                    new StatusEventData(KernelAvailability.Ready, method), vendorData);
                break;
            case "thread/archived":
            case "thread/closed":
            case "thread/deleted":
                Emit(threadId, turnId, itemId, KernelEventKind.SessionClosed,
                    new StatusEventData(KernelAvailability.Ready, method), vendorData);
                break;
            case "turn/started":
                if (parameters.TryGetProperty("turn", out JsonElement startedTurn))
                {
                    turnId = GetString(startedTurn, "id") ?? turnId;
                }

                Emit(threadId, turnId, null, KernelEventKind.TurnStarted,
                    new TurnEventData(TurnStatus.Running), vendorData);
                break;
            case "turn/completed":
                JsonElement completedTurn = parameters.TryGetProperty("turn", out JsonElement turn) ? turn : default;
                turnId = completedTurn.ValueKind == JsonValueKind.Object ? GetString(completedTurn, "id") ?? turnId : turnId;
                TurnStatus status = completedTurn.ValueKind == JsonValueKind.Object ? MapTurnStatus(GetString(completedTurn, "status")) : TurnStatus.Completed;
                Emit(threadId, turnId, null, status switch
                {
                    TurnStatus.Cancelled => KernelEventKind.TurnCancelled,
                    TurnStatus.Failed => KernelEventKind.TurnFailed,
                    _ => KernelEventKind.TurnCompleted
                }, new TurnEventData(status), vendorData);
                break;
            case "turn/diff/updated":
                string diff = GetString(parameters, "diff") ?? string.Empty;
                if (threadId is not null)
                {
                    _diffs[DiffKey(threadId, turnId)] = diff;
                    _diffs[DiffKey(threadId, null)] = diff;
                }

                Emit(threadId, turnId, null, KernelEventKind.ItemUpdated,
                    new ItemEventData(MapDiffItem(turnId ?? "diff", diff, vendorData)), vendorData);
                break;
            case "item/started":
            case "item/completed":
                if (parameters.TryGetProperty("item", out JsonElement item))
                {
                    itemId = GetString(item, "id") ?? itemId;
                    Emit(threadId, turnId, itemId,
                        method == "item/started" ? KernelEventKind.ItemStarted : KernelEventKind.ItemCompleted,
                        new ItemEventData(MapItem(item, method == "item/started" ? KernelItemStatus.Running : KernelItemStatus.Completed)),
                        vendorData);
                }

                break;
            case "item/agentMessage/delta":
                EmitDelta(threadId, turnId, itemId, "text", GetString(parameters, "delta"), vendorData);
                break;
            case "item/plan/delta":
                EmitDelta(threadId, turnId, itemId, "plan", GetString(parameters, "delta"), vendorData);
                break;
            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
                EmitDelta(threadId, turnId, itemId, "reasoning", GetString(parameters, "delta"), vendorData);
                break;
            case "item/commandExecution/outputDelta":
            case "command/exec/outputDelta":
            case "process/outputDelta":
                EmitDelta(threadId, turnId, itemId, "command", GetString(parameters, "delta"), vendorData);
                break;
            case "item/fileChange/outputDelta":
                EmitDelta(threadId, turnId, itemId, "file", GetString(parameters, "delta"), vendorData);
                break;
            case "item/fileChange/patchUpdated":
                JsonElement[] patchChanges = GetArray(parameters, "changes").ToArray();
                string patchDiff = string.Join("\n", patchChanges.Select(change => GetString(change, "diff") ?? string.Empty));
                if (threadId is not null)
                {
                    _diffs[DiffKey(threadId, turnId)] = patchDiff;
                    _diffs[DiffKey(threadId, null)] = patchDiff;
                }

                Emit(threadId, turnId, itemId, KernelEventKind.ItemUpdated,
                    new ItemEventData(MapDiffItem(itemId ?? turnId ?? "diff", patchDiff, vendorData)), vendorData);
                break;
            case "thread/tokenUsage/updated":
                JsonElement usage = parameters.TryGetProperty("tokenUsage", out JsonElement tokenUsage) ? tokenUsage : parameters;
                Emit(threadId, turnId, null, KernelEventKind.UsageUpdated,
                    new UsageEventData(
                        GetInt64(usage, "inputTokens") ?? GetNestedInt64(usage, "total", "inputTokens"),
                        GetInt64(usage, "outputTokens") ?? GetNestedInt64(usage, "total", "outputTokens"),
                        GetInt64(usage, "cachedInputTokens") ?? GetNestedInt64(usage, "total", "cachedInputTokens"),
                        null,
                        null), vendorData);
                break;
            case "error":
            case "warning":
            case "guardianWarning":
            case "deprecationNotice":
            case "configWarning":
                string message = GetString(parameters, "message")
                    ?? (parameters.TryGetProperty("error", out JsonElement error) ? GetString(error, "message") : null)
                    ?? method;
                EmitDiagnostic("CODEX_" + method.Replace('/', '_').ToUpperInvariant(),
                    method == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    message, vendorData, threadId, turnId, itemId);
                break;
            default:
                EmitDiagnostic("CODEX_NOTIFICATION", DiagnosticSeverity.Trace,
                    $"Unmapped Codex notification '{method}'.", vendorData, threadId, turnId, itemId);
                break;
        }

        return Task.CompletedTask;
    }

    private Task OnServerRequestAsync(CodexServerRequest request)
    {
        return request.Method switch
        {
            "item/commandExecution/requestApproval" => RegisterPermissionAsync(request, "command"),
            "item/fileChange/requestApproval" => RegisterPermissionAsync(request, "file-change"),
            "item/permissions/requestApproval" => RegisterPermissionAsync(request, "permissions"),
            "item/tool/requestUserInput" => RegisterToolInputAsync(request),
            "mcpServer/elicitation/request" => RegisterMcpElicitationAsync(request),
            _ => _client.RespondErrorAsync(request.Id, -32601, $"Unsupported Codex server request '{request.Method}'.")
        };
    }

    private Task RegisterPermissionAsync(CodexServerRequest request, string nativeKind)
    {
        string id = InteractionId(request.Id, nativeKind);
        JsonElement parameters = request.Parameters;
        string threadId = GetString(parameters, "threadId") ?? string.Empty;
        string? turnId = GetString(parameters, "turnId");
        string? itemId = GetString(parameters, "itemId");
        var impacts = new List<ResourceImpact>();
        if (nativeKind == "command")
        {
            impacts.Add(new ResourceImpact(
                "process",
                GetString(parameters, "cwd") ?? "workspace",
                GetString(parameters, "command") ?? "execute command",
                "high",
                GetString(parameters, "reason")));
        }
        else if (parameters.TryGetProperty("grantRoot", out JsonElement grantRoot) && grantRoot.ValueKind == JsonValueKind.String)
        {
            impacts.Add(new ResourceImpact("filesystem", grantRoot.GetString()!, "write", "high", GetString(parameters, "reason")));
        }

        var permission = new PermissionRequest(
            id,
            Session(threadId),
            turnId,
            nativeKind,
            nativeKind == "command" ? "Approve command execution" : "Approve file or permission change",
            GetString(parameters, "reason"),
            impacts,
            new[]
            {
                new PermissionChoice("deny", PermissionDecisionKind.Deny, "Deny", "Do not allow this operation."),
                new PermissionChoice("allow-once", PermissionDecisionKind.AllowOnce, "Allow once", "Allow only this request."),
                new PermissionChoice("allow-session", PermissionDecisionKind.AllowForSession, "Allow for session", "Allow matching requests for this session.")
            },
            VendorJson.Sanitize(request.VendorData));
        _interactions[id] = new PendingInteraction(request.Id, InteractionKind.Permission, request.Parameters, nativeKind);
        Emit(threadId, turnId, itemId, KernelEventKind.PermissionRequested, new PermissionEventData(permission), request.VendorData);
        return Task.CompletedTask;
    }

    private Task RegisterToolInputAsync(CodexServerRequest request)
    {
        string id = InteractionId(request.Id, "tool-input");
        JsonElement parameters = request.Parameters;
        string threadId = GetString(parameters, "threadId") ?? string.Empty;
        JsonElement[] questions = GetArray(parameters, "questions").ToArray();
        string prompt = string.Join("\n\n", questions.Select(q => GetString(q, "question") ?? string.Empty));
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = questions.ToDictionary(
                q => GetString(q, "id") ?? Guid.NewGuid().ToString("N"),
                q => new
                {
                    type = "array",
                    items = new { type = "string" },
                    title = GetString(q, "header"),
                    description = GetString(q, "question")
                })
        });
        var elicitation = new ElicitationRequest(
            id,
            Session(threadId),
            "Codex requests input",
            prompt,
            schema,
            VendorJson.Sanitize(request.VendorData));
        _interactions[id] = new PendingInteraction(request.Id, InteractionKind.ToolInput, request.Parameters, "tool-input");
        Emit(threadId, GetString(parameters, "turnId"), GetString(parameters, "itemId"),
            KernelEventKind.ElicitationRequested, new ElicitationEventData(elicitation), request.VendorData);
        return Task.CompletedTask;
    }

    private Task RegisterMcpElicitationAsync(CodexServerRequest request)
    {
        string id = InteractionId(request.Id, "mcp-elicitation");
        JsonElement parameters = request.Parameters;
        string threadId = GetString(parameters, "threadId") ?? string.Empty;
        JsonElement? schema = parameters.TryGetProperty("requestedSchema", out JsonElement requestedSchema)
            ? requestedSchema.Clone()
            : (JsonElement?)null;
        var elicitation = new ElicitationRequest(
            id,
            Session(threadId),
            "Input requested by " + (GetString(parameters, "serverName") ?? "MCP server"),
            GetString(parameters, "message") ?? "Provide the requested input.",
            schema,
            VendorJson.Sanitize(request.VendorData));
        _interactions[id] = new PendingInteraction(request.Id, InteractionKind.McpElicitation, request.Parameters, "mcp-elicitation");
        Emit(threadId, GetString(parameters, "turnId"), null,
            KernelEventKind.ElicitationRequested, new ElicitationEventData(elicitation), request.VendorData);
        return Task.CompletedTask;
    }

    private KernelSessionSummary MapSession(JsonElement thread, string? requestedTitle = null)
    {
        string id = GetString(thread, "id") ?? throw new CodexProtocolException("Thread has no id.");
        DateTimeOffset created = FromUnixSeconds(GetInt64(thread, "createdAt")) ?? DateTimeOffset.UtcNow;
        DateTimeOffset updated = FromUnixSeconds(GetInt64(thread, "updatedAt")) ?? created;
        string title = requestedTitle ?? GetString(thread, "name") ?? GetString(thread, "preview") ?? "Codex thread";
        string? cwd = GetString(thread, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd)) _writableRoots.TryAdd(Path.GetFullPath(cwd), 0);
        return new KernelSessionSummary(
            Session(id, GetString(thread, "forkedFromId") ?? GetString(thread, "parentThreadId")),
            title,
            MapSessionStatus(thread),
            created,
            updated,
            GetString(thread, "preview"),
            new Dictionary<string, string>
            {
                ["cwd"] = cwd ?? string.Empty,
                ["modelProvider"] = GetString(thread, "modelProvider") ?? string.Empty,
                ["cliVersion"] = GetString(thread, "cliVersion") ?? string.Empty
            });
    }

    private void TrackWorkspace(WorkspaceDescriptor workspace)
    {
        foreach (string root in workspace.AdditionalRoots.Prepend(workspace.RootPath))
            _writableRoots.TryAdd(Path.GetFullPath(root), 0);
    }

    private static KernelTurn MapTurn(SessionRef session, JsonElement turn)
    {
        string id = GetString(turn, "id") ?? throw new CodexProtocolException("Turn has no id.");
        DateTimeOffset startedAt = FromUnixMilliseconds(GetInt64(turn, "startedAt")) ?? DateTimeOffset.UtcNow;
        return new KernelTurn(session, id, MapTurnStatus(GetString(turn, "status")), startedAt);
    }

    private static KernelItem MapItem(JsonElement item, KernelItemStatus? forcedStatus)
    {
        string id = GetString(item, "id") ?? "unknown";
        string type = GetString(item, "type") ?? "unknown";
        KernelItemKind kind = type switch
        {
            "userMessage" => KernelItemKind.UserMessage,
            "agentMessage" => KernelItemKind.AssistantMessage,
            "plan" => KernelItemKind.Plan,
            "reasoning" => KernelItemKind.Reasoning,
            "commandExecution" => KernelItemKind.CommandExecution,
            "fileChange" => KernelItemKind.FileChange,
            "mcpToolCall" or "dynamicToolCall" or "webSearch" => KernelItemKind.ToolCall,
            "collabAgentToolCall" or "subAgentActivity" => KernelItemKind.Subagent,
            _ => KernelItemKind.Notice
        };
        KernelItemStatus status = forcedStatus ?? MapItemStatus(GetString(item, "status"));
        IReadOnlyList<ContentBlock> content = MapItemContent(item, type, id);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new KernelItem(id, kind, status, type, content, now, now, VendorData: VendorJson.Sanitize(item));
    }

    private static IReadOnlyList<ContentBlock> MapItemContent(JsonElement item, string type, string id)
    {
        var content = new List<ContentBlock>();
        if (type is "agentMessage" or "plan")
        {
            content.Add(type == "plan"
                ? new TextContentBlock(GetString(item, "text") ?? string.Empty)
                : new TextContentBlock(GetString(item, "text") ?? string.Empty));
        }
        else if (type == "reasoning")
        {
            string summary = item.TryGetProperty("summary", out JsonElement summaries) && summaries.ValueKind == JsonValueKind.Array
                ? string.Join("\n", summaries.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            content.Add(new ReasoningContentBlock(summary));
        }
        else if (type == "userMessage" && item.TryGetProperty("content", out JsonElement userContent))
        {
            foreach (JsonElement value in userContent.EnumerateArray())
            {
                if (GetString(value, "type") == "text")
                {
                    content.Add(new TextContentBlock(GetString(value, "text") ?? string.Empty));
                }
            }
        }
        else if (type == "commandExecution")
        {
            string command = GetString(item, "command") ?? string.Empty;
            content.Add(new ToolCallContentBlock(id, "command", JsonSerializer.SerializeToElement(new { command, cwd = GetString(item, "cwd") }), command));
            if (GetString(item, "aggregatedOutput") is { } output)
            {
                content.Add(new ToolResultContentBlock(id, GetInt64(item, "exitCode") is 0, output));
            }
        }
        else if (type == "fileChange" && item.TryGetProperty("changes", out JsonElement changes))
        {
            foreach (JsonElement change in changes.EnumerateArray())
            {
                string path = GetString(change, "path") ?? "unknown";
                content.Add(new FileChangeContentBlock(
                    path,
                    GetString(change, "kind") ?? "update"));
                if (GetString(change, "diff") is { Length: > 0 } diff)
                {
                    content.Add(new DiffContentBlock(diff, new[] { path }));
                }
            }
        }
        else
        {
            content.Add(new NoticeContentBlock(type, "Codex vendor item retained in VendorData.", DiagnosticSeverity.Information));
        }

        return content;
    }

    private static KernelItem MapDiffItem(string id, string diff, JsonElement vendorData)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new KernelItem(
            id,
            KernelItemKind.Diff,
            KernelItemStatus.Completed,
            "Working tree diff",
            new ContentBlock[] { new DiffContentBlock(diff, ParseDiffPaths(diff)) },
            now,
            now,
            VendorData: VendorJson.Sanitize(vendorData));
    }

    private void EmitDelta(string? threadId, string? turnId, string? itemId, string channel, string? delta, JsonElement vendorData)
    {
        if (delta is not null)
        {
            Emit(threadId, turnId, itemId, KernelEventKind.ContentDelta,
                new ContentDeltaEventData(channel, delta, channel == "text" ? "markdown" : "text"), vendorData);
        }
    }

    private void Emit(
        string? threadId,
        string? turnId,
        string? itemId,
        KernelEventKind kind,
        KernelEventData data,
        JsonElement vendorData)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        try
        {
            _events.TryWrite(new KernelEvent(
                AdapterId,
                _profile.ProfileId,
                threadId,
                turnId,
                itemId,
                sequence,
                DateTimeOffset.UtcNow,
                kind,
                data,
                VendorJson.Sanitize(vendorData)));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
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
            AdapterId,
            _profile.ProfileId,
            next.NativeSessionId,
            next.NativeTurnId,
            null,
            next.Sequence,
            next.Timestamp,
            KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic(
                "CODEX_EVENT_DELTA_GAP",
                DiagnosticSeverity.Warning,
                $"Dropped {gap} streaming content delta event(s) under sustained backpressure; refresh canonical history to reconcile complete content.")));
    }

    private void EmitDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        JsonElement vendorData,
        string? threadId = null,
        string? turnId = null,
        string? itemId = null) =>
        Emit(threadId, turnId, itemId, KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic(code, severity, CodexAppServerClient.Redact(message), VendorData: VendorJson.Sanitize(vendorData))),
            vendorData);

    private void OnDiagnostic(string message)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        EmitDiagnostic("CODEX_STDIO", DiagnosticSeverity.Warning, message, empty);
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_availability != KernelAvailability.Ready)
        {
            throw new InvalidOperationException("Codex adapter has not been initialized.");
        }
    }

    private void ValidateSession(SessionRef session)
    {
        EnsureReady();
        if (!string.Equals(session.AdapterId, AdapterId, StringComparison.Ordinal) ||
            !string.Equals(session.ProfileId, _profile.ProfileId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Session belongs to a different adapter or profile.", nameof(session));
        }
    }

    private SessionRef Session(string nativeId, string? parent = null) =>
        new(AdapterId, _profile.ProfileId, nativeId, parent);

    private static void ValidatePage(PageRequest request)
    {
        if (request.PageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Page size must be between 1 and 1000.");
        }
    }

    private static object[] MapInput(TurnInput input)
    {
        var items = new List<object>();
        foreach (ContentBlock content in input.Content)
        {
            switch (content)
            {
                case TextContentBlock text:
                    items.Add(new { type = "text", text = text.Text });
                    break;
                case ImageContentBlock image when Uri.TryCreate(image.Uri, UriKind.Absolute, out _):
                    items.Add(new { type = "image", url = image.Uri });
                    break;
                default:
                    throw new NotSupportedException($"Codex turn input does not support {content.GetType().Name}.");
            }
        }

        foreach (string path in input.ReferencedPaths ?? [])
        {
            items.Add(new { type = "mention", name = Path.GetFileName(path), path });
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("Turn input must contain at least one supported content block.", nameof(input));
        }

        return [.. items];
    }

    private static string? NormalizeApprovalMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" => null,
        "untrusted" => "untrusted",
        "on-request" or "onrequest" => "on-request",
        "never" => "never",
        _ => throw new NotSupportedException($"Codex approval mode '{mode}' is not in schema 0.147.0.")
    };

    private static object? MapSandbox(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" => null,
        "danger-full-access" or "dangerfullaccess" => new { type = "dangerFullAccess" },
        "read-only" or "readonly" => new { type = "readOnly" },
        "workspace-write" or "workspacewrite" => new { type = "workspaceWrite" },
        _ => throw new NotSupportedException($"Codex sandbox mode '{mode}' is not in schema 0.147.0.")
    };

    private static SessionStatus MapSessionStatus(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out JsonElement status))
        {
            return SessionStatus.Idle;
        }

        string? type = status.ValueKind == JsonValueKind.String ? status.GetString() : GetString(status, "type");
        if (type == "systemError")
        {
            return SessionStatus.Failed;
        }

        if (type == "active")
        {
            if (status.TryGetProperty("activeFlags", out JsonElement flags) && flags.ValueKind == JsonValueKind.Array &&
                flags.EnumerateArray().Any(flag => flag.GetString() == "waitingOnApproval"))
            {
                return SessionStatus.WaitingForApproval;
            }

            return SessionStatus.Running;
        }

        return SessionStatus.Idle;
    }

    private static TurnStatus MapTurnStatus(string? status) => status switch
    {
        "completed" => TurnStatus.Completed,
        "interrupted" => TurnStatus.Cancelled,
        "failed" => TurnStatus.Failed,
        _ => TurnStatus.Running
    };

    private static KernelItemStatus MapItemStatus(string? status) => status switch
    {
        "completed" => KernelItemStatus.Completed,
        "failed" => KernelItemStatus.Failed,
        "declined" or "cancelled" or "interrupted" => KernelItemStatus.Cancelled,
        "inProgress" or "running" => KernelItemStatus.Running,
        _ => KernelItemStatus.Pending
    };

    private static JsonElement RequiredProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
            ? property.Clone()
            : throw new CodexProtocolException($"Codex result has no '{name}' property.");

    private static IEnumerable<JsonElement> GetArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(item => item.Clone())
            : [];

    private static string? GetString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static bool? GetBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static long? GetInt64(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result)
            ? result
            : null;

    private static long? GetNestedInt64(JsonElement value, string parent, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out JsonElement nested)
            ? GetInt64(nested, name)
            : null;

    private static DateTimeOffset? FromUnixSeconds(long? value) => value is null ? null : DateTimeOffset.FromUnixTimeSeconds(value.Value);

    private static DateTimeOffset? FromUnixMilliseconds(long? value) => value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static string InteractionId(JsonElement id, string kind) => $"codex:{kind}:{id.GetRawText()}";

    private static string DiffKey(string threadId, string? turnId) => threadId + "\0" + (turnId ?? string.Empty);

    private static IReadOnlyList<string> ParseDiffPaths(string diff) => diff.Split('\n')
        .Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal) || line.StartsWith("--- a/", StringComparison.Ordinal))
        .Select(line => line[6..].TrimEnd('\r'))
        .Where(path => path != "/dev/null")
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string? ExtractThreadDiff(JsonElement result, string? nativeTurnOrItemId)
    {
        if (!result.TryGetProperty("thread", out JsonElement thread) ||
            !thread.TryGetProperty("turns", out JsonElement turns) ||
            turns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fragments = new List<string>();
        foreach (JsonElement turn in turns.EnumerateArray())
        {
            string? turnId = GetString(turn, "id");
            if (nativeTurnOrItemId is not null && turnId != nativeTurnOrItemId)
            {
                bool containsItem = turn.TryGetProperty("items", out JsonElement candidateItems) &&
                    candidateItems.ValueKind == JsonValueKind.Array &&
                    candidateItems.EnumerateArray().Any(item => GetString(item, "id") == nativeTurnOrItemId);
                if (!containsItem)
                {
                    continue;
                }
            }

            if (!turn.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in items.EnumerateArray().Where(item => GetString(item, "type") == "fileChange"))
            {
                if (!item.TryGetProperty("changes", out JsonElement changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                fragments.AddRange(changes.EnumerateArray()
                    .Select(change => GetString(change, "diff"))
                    .Where(diff => !string.IsNullOrEmpty(diff))
                    .Select(diff => diff!));
            }
        }

        return fragments.Count == 0 ? null : string.Join("\n", fragments);
    }

    private static string[] ToStringArray(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Select(item => item.ToString()).ToArray(),
        JsonValueKind.String => [value.GetString()!],
        _ => [value.ToString()]
    };

    private static IReadOnlyList<AuthenticationMethod> SupportedAuthenticationMethods { get; } =
        new[] { AuthenticationMethod.Browser, AuthenticationMethod.DeviceCode, AuthenticationMethod.ApiKey };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _availability = KernelAvailability.Stopped;
        _lifetime.Cancel();
        _events.Complete();
        await _client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private enum InteractionKind
    {
        Permission,
        ToolInput,
        McpElicitation
    }

    private sealed record PendingInteraction(
        JsonElement RequestId,
        InteractionKind Kind,
        JsonElement Parameters,
        string NativeKind);
}
