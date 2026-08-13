using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Codex;

public sealed class CodexKernelAdapter : IKernelAdapter
{
    public const string CodexAdapterId = "codex";
    private readonly KernelProfile _profile;
    private readonly CodexAppServerClient _client;
    private readonly Channel<KernelEvent> _events = Channel.CreateUnbounded<KernelEvent>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<string, PendingInteraction> _interactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _diffs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private long _sequence;
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
            Array.Empty<string>(),
            NetworkRestricted: false,
            ProcessRestricted: false,
            IsVerifiedByHost: false,
            "Codex enforces the selected sandbox and approval policy; the host displays and correlates approvals."),
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
            var result = await _client.InitializeAsync(context.HostVersion, cancellationToken).ConfigureAwait(false);
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
        var diagnostics = _availability == KernelAvailability.Ready
            ? Array.Empty<KernelDiagnostic>()
            : new[] { new KernelDiagnostic("CODEX_NOT_READY", DiagnosticSeverity.Warning, "Codex App Server is not ready.") };
        return Task.FromResult(new KernelHealth(
            _availability,
            _availability == KernelAvailability.Ready ? "Codex App Server is ready." : "Codex App Server is unavailable.",
            DateTimeOffset.UtcNow,
            diagnostics));
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var result = await _client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
        {
            return new AuthenticationState(
                AuthenticationStatus.SignedOut,
                null,
                null,
                SupportedAuthenticationMethods,
                GetBoolean(result, "requiresOpenaiAuth") == true ? "OpenAI authentication is required." : null);
        }

        var type = GetString(account, "type");
        var label = type == "chatgpt" ? GetString(account, "email") : type;
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
        var result = await _client.RequestAsync("account/login/start", parameters, cancellationToken)
            .ConfigureAwait(false);
        var type = GetString(result, "type");
        var loginId = GetString(result, "loginId") ?? throw new CodexProtocolException("Login response has no loginId.");
        var uriText = type == "chatgpt" ? GetString(result, "authUrl") : GetString(result, "verificationUrl");
        var uri = Uri.TryCreate(uriText, UriKind.Absolute, out var parsedUri) ? parsedUri : null;
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
            var result = await _client.RequestAsync(
                "model/list",
                new { cursor, includeHidden = false, limit = 100 },
                cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in data.EnumerateArray())
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
        var result = await _client.RequestAsync(
            "thread/list",
            new { cursor = request.Cursor, limit = request.PageSize, archived = false, sortKey = "updated_at", sortDirection = "desc" },
            cancellationToken).ConfigureAwait(false);
        var items = GetArray(result, "data").Select(thread => MapSession(thread)).ToArray();
        var next = GetString(result, "nextCursor");
        return new ResultPage<KernelSessionSummary>(items, next, next is not null);
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var result = await _client.RequestAsync(
            "thread/start",
            new
            {
                cwd = request.Workspace.RootPath,
                runtimeWorkspaceRoots = request.Workspace.AdditionalRoots.Prepend(request.Workspace.RootPath).Distinct().ToArray(),
                model = request.ModelId,
                experimentalRawEvents = false
            },
            cancellationToken).ConfigureAwait(false);
        var thread = RequiredProperty(result, "thread");
        var summary = MapSession(thread, request.Title);
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            await _client.RequestAsync("thread/name/set", new { threadId = summary.Session.NativeSessionId, name = request.Title }, cancellationToken)
                .ConfigureAwait(false);
            summary = summary with { Title = request.Title! };
        }

        return summary;
    }

    public async Task<KernelSessionSummary> ResumeSessionAsync(
        SessionRef session,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        var result = await _client.RequestAsync(
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
        var result = await _client.RequestAsync(
            "thread/fork",
            new { threadId = request.Session.NativeSessionId, lastTurnId = request.NativeItemId },
            cancellationToken).ConfigureAwait(false);
        var summary = MapSession(RequiredProperty(result, "thread"), request.Title);
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            await _client.RequestAsync("thread/name/set", new { threadId = summary.Session.NativeSessionId, name = request.Title }, cancellationToken)
                .ConfigureAwait(false);
            summary = summary with { Title = request.Title! };
        }

        return summary;
    }

    public async Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        await _client.RequestAsync("thread/archive", new { threadId = session.NativeSessionId }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<KernelTurn> StartTurnAsync(
        SessionRef session,
        TurnInput input,
        TurnOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        var result = await _client.RequestAsync(
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
        var turn = RequiredProperty(result, "turn");
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
        if (!_interactions.TryRemove(response.PermissionId, out var pending) || pending.Kind != InteractionKind.Permission)
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
            var requestedPermissions = pending.Parameters.TryGetProperty("permissions", out var permissions)
                ? permissions.Clone()
                : JsonSerializer.SerializeToElement(new { });
            var grantedPermissions = response.ChoiceId == "deny"
                ? JsonSerializer.SerializeToElement(new { })
                : requestedPermissions;
            var scope = response.ChoiceId switch
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

        var decision = response.ChoiceId switch
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
        if (!_interactions.TryRemove(response.RequestId, out var pending) || pending.Kind == InteractionKind.Permission)
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
                var answers = response.Value is { ValueKind: JsonValueKind.Object }
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
        var result = await _client.RequestAsync(
            "thread/items/list",
            new { threadId = session.NativeSessionId, cursor = request.Cursor, limit = request.PageSize, sortDirection = "asc" },
            cancellationToken).ConfigureAwait(false);
        var items = GetArray(result, "data").Select(item => MapItem(item, null)).ToArray();
        var next = GetString(result, "nextCursor");
        return new ResultPage<KernelItem>(items, next, next is not null);
    }

    public async Task<KernelDiff?> ReadDiffAsync(
        SessionRef session,
        string? nativeTurnOrItemId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        var key = DiffKey(session.NativeSessionId, nativeTurnOrItemId);
        if (!_diffs.TryGetValue(key, out var diff) && nativeTurnOrItemId is not null)
        {
            _diffs.TryGetValue(DiffKey(session.NativeSessionId, null), out diff);
        }

        if (diff is null)
        {
            var result = await _client.RequestAsync(
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
        await foreach (var item in _events.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private Task OnNotificationAsync(string method, JsonElement parameters, JsonElement vendorData)
    {
        var threadId = GetString(parameters, "threadId");
        var turnId = GetString(parameters, "turnId");
        var itemId = GetString(parameters, "itemId");
        switch (method)
        {
            case "account/updated":
            case "account/login/completed":
                Emit(threadId, turnId, itemId, KernelEventKind.AuthenticationChanged,
                    new StatusEventData(KernelAvailability.Ready, method == "account/updated" ? "Codex account changed." : "Codex login completed."), vendorData);
                break;
            case "thread/started":
                if (parameters.TryGetProperty("thread", out var startedThread))
                {
                    var summary = MapSession(startedThread);
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
                if (parameters.TryGetProperty("turn", out var startedTurn))
                {
                    turnId = GetString(startedTurn, "id") ?? turnId;
                }

                Emit(threadId, turnId, null, KernelEventKind.TurnStarted,
                    new TurnEventData(TurnStatus.Running), vendorData);
                break;
            case "turn/completed":
                var completedTurn = parameters.TryGetProperty("turn", out var turn) ? turn : default;
                turnId = completedTurn.ValueKind == JsonValueKind.Object ? GetString(completedTurn, "id") ?? turnId : turnId;
                var status = completedTurn.ValueKind == JsonValueKind.Object ? MapTurnStatus(GetString(completedTurn, "status")) : TurnStatus.Completed;
                Emit(threadId, turnId, null, status switch
                {
                    TurnStatus.Cancelled => KernelEventKind.TurnCancelled,
                    TurnStatus.Failed => KernelEventKind.TurnFailed,
                    _ => KernelEventKind.TurnCompleted
                }, new TurnEventData(status), vendorData);
                break;
            case "turn/diff/updated":
                var diff = GetString(parameters, "diff") ?? string.Empty;
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
                if (parameters.TryGetProperty("item", out var item))
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
                var patchChanges = GetArray(parameters, "changes").ToArray();
                var patchDiff = string.Join("\n", patchChanges.Select(change => GetString(change, "diff") ?? string.Empty));
                if (threadId is not null)
                {
                    _diffs[DiffKey(threadId, turnId)] = patchDiff;
                    _diffs[DiffKey(threadId, null)] = patchDiff;
                }

                Emit(threadId, turnId, itemId, KernelEventKind.ItemUpdated,
                    new ItemEventData(MapDiffItem(itemId ?? turnId ?? "diff", patchDiff, vendorData)), vendorData);
                break;
            case "thread/tokenUsage/updated":
                var usage = parameters.TryGetProperty("tokenUsage", out var tokenUsage) ? tokenUsage : parameters;
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
                var message = GetString(parameters, "message")
                    ?? (parameters.TryGetProperty("error", out var error) ? GetString(error, "message") : null)
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
        var id = InteractionId(request.Id, nativeKind);
        var parameters = request.Parameters;
        var threadId = GetString(parameters, "threadId") ?? string.Empty;
        var turnId = GetString(parameters, "turnId");
        var itemId = GetString(parameters, "itemId");
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
        else if (parameters.TryGetProperty("grantRoot", out var grantRoot) && grantRoot.ValueKind == JsonValueKind.String)
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
        var id = InteractionId(request.Id, "tool-input");
        var parameters = request.Parameters;
        var threadId = GetString(parameters, "threadId") ?? string.Empty;
        var questions = GetArray(parameters, "questions").ToArray();
        var prompt = string.Join("\n\n", questions.Select(q => GetString(q, "question") ?? string.Empty));
        var schema = JsonSerializer.SerializeToElement(new
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
        var id = InteractionId(request.Id, "mcp-elicitation");
        var parameters = request.Parameters;
        var threadId = GetString(parameters, "threadId") ?? string.Empty;
        var schema = parameters.TryGetProperty("requestedSchema", out var requestedSchema)
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
        var id = GetString(thread, "id") ?? throw new CodexProtocolException("Thread has no id.");
        var created = FromUnixSeconds(GetInt64(thread, "createdAt")) ?? DateTimeOffset.UtcNow;
        var updated = FromUnixSeconds(GetInt64(thread, "updatedAt")) ?? created;
        var title = requestedTitle ?? GetString(thread, "name") ?? GetString(thread, "preview") ?? "Codex thread";
        return new KernelSessionSummary(
            Session(id, GetString(thread, "forkedFromId") ?? GetString(thread, "parentThreadId")),
            title,
            MapSessionStatus(thread),
            created,
            updated,
            GetString(thread, "preview"),
            new Dictionary<string, string>
            {
                ["cwd"] = GetString(thread, "cwd") ?? string.Empty,
                ["modelProvider"] = GetString(thread, "modelProvider") ?? string.Empty,
                ["cliVersion"] = GetString(thread, "cliVersion") ?? string.Empty
            });
    }

    private KernelTurn MapTurn(SessionRef session, JsonElement turn)
    {
        var id = GetString(turn, "id") ?? throw new CodexProtocolException("Turn has no id.");
        var startedAt = FromUnixMilliseconds(GetInt64(turn, "startedAt")) ?? DateTimeOffset.UtcNow;
        return new KernelTurn(session, id, MapTurnStatus(GetString(turn, "status")), startedAt);
    }

    private KernelItem MapItem(JsonElement item, KernelItemStatus? forcedStatus)
    {
        var id = GetString(item, "id") ?? "unknown";
        var type = GetString(item, "type") ?? "unknown";
        var kind = type switch
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
        var status = forcedStatus ?? MapItemStatus(GetString(item, "status"));
        var content = MapItemContent(item, type, id);
        var now = DateTimeOffset.UtcNow;
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
            var summary = item.TryGetProperty("summary", out var summaries) && summaries.ValueKind == JsonValueKind.Array
                ? string.Join("\n", summaries.EnumerateArray().Select(value => value.GetString()))
                : string.Empty;
            content.Add(new ReasoningContentBlock(summary));
        }
        else if (type == "userMessage" && item.TryGetProperty("content", out var userContent))
        {
            foreach (var value in userContent.EnumerateArray())
            {
                if (GetString(value, "type") == "text")
                {
                    content.Add(new TextContentBlock(GetString(value, "text") ?? string.Empty));
                }
            }
        }
        else if (type == "commandExecution")
        {
            var command = GetString(item, "command") ?? string.Empty;
            content.Add(new ToolCallContentBlock(id, "command", JsonSerializer.SerializeToElement(new { command, cwd = GetString(item, "cwd") }), command));
            if (GetString(item, "aggregatedOutput") is { } output)
            {
                content.Add(new ToolResultContentBlock(id, GetInt64(item, "exitCode") is 0, output));
            }
        }
        else if (type == "fileChange" && item.TryGetProperty("changes", out var changes))
        {
            foreach (var change in changes.EnumerateArray())
            {
                var path = GetString(change, "path") ?? "unknown";
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
        var now = DateTimeOffset.UtcNow;
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
        var sequence = Interlocked.Increment(ref _sequence);
        _events.Writer.TryWrite(new KernelEvent(
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
        var empty = JsonSerializer.SerializeToElement(new { });
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
        foreach (var content in input.Content)
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

        foreach (var path in input.ReferencedPaths ?? Array.Empty<string>())
        {
            items.Add(new { type = "mention", name = Path.GetFileName(path), path });
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("Turn input must contain at least one supported content block.", nameof(input));
        }

        return items.ToArray();
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
        if (!thread.TryGetProperty("status", out var status))
        {
            return SessionStatus.Idle;
        }

        var type = status.ValueKind == JsonValueKind.String ? status.GetString() : GetString(status, "type");
        if (type == "systemError")
        {
            return SessionStatus.Failed;
        }

        if (type == "active")
        {
            if (status.TryGetProperty("activeFlags", out var flags) && flags.ValueKind == JsonValueKind.Array &&
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
        value.TryGetProperty(name, out var property)
            ? property.Clone()
            : throw new CodexProtocolException($"Codex result has no '{name}' property.");

    private static IEnumerable<JsonElement> GetArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(item => item.Clone())
            : Enumerable.Empty<JsonElement>();

    private static string? GetString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static bool? GetBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static long? GetInt64(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result)
            ? result
            : null;

    private static long? GetNestedInt64(JsonElement value, string parent, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out var nested)
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
        if (!result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fragments = new List<string>();
        foreach (var turn in turns.EnumerateArray())
        {
            var turnId = GetString(turn, "id");
            if (nativeTurnOrItemId is not null && turnId != nativeTurnOrItemId)
            {
                var containsItem = turn.TryGetProperty("items", out var candidateItems) &&
                    candidateItems.ValueKind == JsonValueKind.Array &&
                    candidateItems.EnumerateArray().Any(item => GetString(item, "id") == nativeTurnOrItemId);
                if (!containsItem)
                {
                    continue;
                }
            }

            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray().Where(item => GetString(item, "type") == "fileChange"))
            {
                if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
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
        JsonValueKind.String => new[] { value.GetString()! },
        _ => new[] { value.ToString() }
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
        _events.Writer.TryComplete();
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
