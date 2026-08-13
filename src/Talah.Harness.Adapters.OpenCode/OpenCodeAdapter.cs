using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode;

public sealed class OpenCodeAdapter : IKernelAdapter
{
    public const string Id = "opencode-server";
    private static readonly KernelCapabilities Capabilities = new(
        CanAuthenticate: true, CanUseApiKey: true, CanListSessions: true, CanResumeSessions: true,
        CanForkSessions: true, CanArchiveSessions: true, CanSteerActiveTurn: false, CanCancelTurn: true,
        CanApproveTools: true, CanAmendToolInput: false, CanReadHistory: true, CanReturnDiffs: true,
        CanConfigureProviders: true, CanConfigureMcp: false, CanUseSubagents: true, CanReplayEvents: false);

    private readonly IOpenCodeExecutableDiscovery _discovery;
    private readonly IOpenCodeProcessSupervisor _supervisor;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan? _startupTimeout;
    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingLogin> _logins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _permissionDirectories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _questionDirectories = new(StringComparer.Ordinal);
    private KernelInitializationContext? _context;
    private OpenCodeExecutableInfo? _executable;
    private OpenCodeServerConnection? _connection;
    private OpenCodeApiClient? _api;
    private OpenCodeSseClient? _sse;
    private OpenCodeEventNormalizer? _normalizer;
    private KernelAvailability _availability = KernelAvailability.Unknown;

    public OpenCodeAdapter(
        IOpenCodeExecutableDiscovery? discovery = null,
        IOpenCodeProcessSupervisor? supervisor = null,
        HttpMessageHandler? handler = null,
        TimeSpan? startupTimeout = null)
    {
        _discovery = discovery ?? new OpenCodeExecutableDiscovery();
        _supervisor = supervisor ?? new OpenCodeProcessSupervisor();
        _handler = handler;
        _startupTimeout = startupTimeout;
    }

    public string AdapterId => Id;

    public KernelDescriptor Descriptor => new(
        Id, "OpenCode Server", "OpenCode HTTP/OpenAPI + SSE", "1.0.0", _executable?.Version,
        _availability, Capabilities, Security(),
        new Dictionary<string, string>
        {
            ["pinnedVersion"] = OpenCodeRelease.Version,
            ["schema"] = OpenCodeRelease.OpenApiSchemaFile,
            ["transport"] = "native-http-sse",
            ["eventReplay"] = "not-supported-by-server"
        });

    public async ValueTask InitializeAsync(KernelInitializationContext context, CancellationToken cancellationToken = default)
    {
        if (_connection is not null) return;
        _context = context;
        _normalizer = new OpenCodeEventNormalizer(context.Profile.ProfileId);
        _executable = await _discovery.DiscoverAsync(context.Profile.Environment, cancellationToken).ConfigureAwait(false);
        if (_executable.State == OpenCodeExecutableState.NotInstalled)
        {
            _availability = KernelAvailability.NotInstalled;
            return;
        }

        if (_executable.State == OpenCodeExecutableState.Incompatible)
        {
            _availability = KernelAvailability.Failed;
            return;
        }

        _availability = KernelAvailability.Starting;
        try
        {
            var launcher = new OpenCodeServerLauncher(_supervisor, _handler, _startupTimeout);
            _connection = await launcher.StartAsync(_executable, context.Profile.DataRoot, context.Profile.Environment, cancellationToken).ConfigureAwait(false);
            _api = new OpenCodeApiClient(_connection.BaseUri, _connection.Username, _connection.Password, _handler);
            _sse = new OpenCodeSseClient(_api);
            _availability = KernelAvailability.Ready;
        }
        catch
        {
            _availability = KernelAvailability.Failed;
            throw;
        }
    }

    public async Task<KernelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_executable is null)
            return Health(KernelAvailability.Unknown, "OpenCode adapter is not initialized.", "OpenCode.NotInitialized", DiagnosticSeverity.Warning);
        if (_executable.State == OpenCodeExecutableState.NotInstalled)
            return Health(KernelAvailability.NotInstalled, _executable.Message, "OpenCode.NotInstalled", DiagnosticSeverity.Error,
                "Install the pinned Windows asset or configure OPENCODE_EXECUTABLE.");
        if (_executable.State == OpenCodeExecutableState.Incompatible)
            return Health(KernelAvailability.Failed, _executable.Message, "OpenCode.IncompatibleVersion", DiagnosticSeverity.Error,
                $"Install OpenCode {OpenCodeRelease.Version}.");
        if (_connection?.Process.HasExited == true)
        {
            _availability = KernelAvailability.Failed;
            return Health(_availability, "OpenCode Server exited unexpectedly.", "OpenCode.ServerExited", DiagnosticSeverity.Critical,
                "Restart the kernel profile.");
        }

        try
        {
            var health = await Api().GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var metadata = await GetServerMetadataAsync(null, cancellationToken).ConfigureAwait(false);
            _availability = health.Healthy ? KernelAvailability.Ready : KernelAvailability.Degraded;
            var vendor = JsonSerializer.SerializeToElement(new
            {
                version = health.Version,
                mcpServers = metadata.Mcp.Keys.Order(StringComparer.Ordinal).ToArray(),
                agentCount = metadata.Agents.Count
            });
            return new KernelHealth(_availability, $"OpenCode Server {health.Version} is {(health.Healthy ? "healthy" : "degraded")}.",
                DateTimeOffset.UtcNow, new[] { new KernelDiagnostic("OpenCode.Server", DiagnosticSeverity.Information, "Authenticated loopback health check succeeded.", VendorData: vendor) });
        }
        catch (Exception ex) when (ex is HttpRequestException or OpenCodeAdapterException)
        {
            _availability = KernelAvailability.Degraded;
            return Health(_availability, "OpenCode Server health check failed.", "OpenCode.HealthFailed", DiagnosticSeverity.Error,
                "Restart the profile. " + Redaction.Redact(ex.Message));
        }
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        var providers = await Api().ListProvidersAsync(null, cancellationToken).ConfigureAwait(false);
        return new AuthenticationState(providers.Connected.Count > 0 ? AuthenticationStatus.SignedIn : AuthenticationStatus.SignedOut,
            providers.Connected.Count > 0 ? string.Join(", ", providers.Connected) : null, null,
            new[] { AuthenticationMethod.Browser, AuthenticationMethod.ApiKey, AuthenticationMethod.VendorDefined },
            providers.Connected.Count == 0 ? "No OpenCode provider is authenticated." : null);
    }

    public async Task<LoginChallenge> BeginLoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Method is not (AuthenticationMethod.Browser or AuthenticationMethod.VendorDefined))
            throw new NotSupportedException("OpenCode Server login supports provider OAuth/browser methods; use ConfigureApiKeyAsync for API keys.");
        var parameters = request.Parameters ?? throw new ArgumentException("Login parameters must include providerId.", nameof(request));
        if (!parameters.TryGetValue("providerId", out var providerId) || string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Login parameter 'providerId' is required.", nameof(request));
        var methodIndex = parameters.TryGetValue("methodIndex", out var indexText) && int.TryParse(indexText, out var index) ? index : 0;
        var directory = parameters.GetValueOrDefault("directory");
        var inputs = parameters.Where(x => x.Key.StartsWith("input.", StringComparison.Ordinal))
            .ToDictionary(x => x.Key[6..], x => x.Value, StringComparer.Ordinal);
        var authorization = await Api().BeginOAuthAsync(providerId, methodIndex, inputs.Count == 0 ? null : inputs, directory, cancellationToken).ConfigureAwait(false);
        var loginId = "login_" + Guid.NewGuid().ToString("N");
        _logins[loginId] = new PendingLogin(providerId, methodIndex, directory);
        return new LoginChallenge(loginId, request.Method, Uri.TryCreate(authorization.Url, UriKind.Absolute, out var uri) ? uri : null,
            null, null, authorization.Instructions ?? "Complete provider authorization, then call CompleteOAuthAsync with the returned code.");
    }

    public Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logins.TryRemove(loginId, out _);
        return Task.CompletedTask;
    }

    public async Task CompleteOAuthAsync(string loginId, string? code, CancellationToken cancellationToken = default)
    {
        if (!_logins.TryRemove(loginId, out var pending)) throw new ArgumentException("Unknown or cancelled login ID.", nameof(loginId));
        if (!await Api().CompleteOAuthAsync(pending.ProviderId, pending.MethodIndex, code, pending.Directory, cancellationToken).ConfigureAwait(false))
            throw new OpenCodeAdapterException("OpenCode rejected the OAuth callback.");
    }

    public async Task ConfigureApiKeyAsync(ApiKeyCredential credential, CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, string>(credential.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (credential.BaseUri is not null) metadata["baseURL"] = credential.BaseUri.ToString();
        if (!await Api().SetApiKeyAsync(credential.ProviderId, credential.Secret, metadata.Count == 0 ? null : metadata, cancellationToken).ConfigureAwait(false))
            throw new OpenCodeAdapterException("OpenCode rejected the provider API key.");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var providers = await Api().ListProvidersAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (var provider in providers.Connected)
            await Api().RemoveAuthAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KernelModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var providers = await Api().ListProvidersAsync(null, cancellationToken).ConfigureAwait(false);
        return providers.All.SelectMany(provider => provider.Models.Values.Select(model => new KernelModel(
            $"{provider.Id}/{model.Id}", model.Name, provider.Name,
            providers.Default.TryGetValue(provider.Id, out var defaultModel) && defaultModel == model.Id,
            new Dictionary<string, string> { ["providerId"] = provider.Id, ["nativeModelId"] = model.Id }))).ToArray();
    }

    public async Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(request.PageSize, 1, 200);
        var sessions = await Api().ListSessionsAsync(null, size, request.Cursor, cancellationToken).ConfigureAwait(false);
        var items = sessions.Select(ToSession).ToArray();
        return new ResultPage<KernelSessionSummary>(items, items.Length == size ? sessions[^1].Time.Updated.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, items.Length == size);
    }

    public async Task<KernelSessionSummary> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var native = await Api().CreateSessionAsync(request.Workspace.RootPath, request.Title, request.AgentId, ParseModel(request.ModelId), cancellationToken).ConfigureAwait(false);
        _directories[native.Id] = request.Workspace.RootPath;
        return ToSession(native);
    }

    public async Task<KernelSessionSummary> ResumeSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        Validate(session);
        var native = await Api().GetSessionAsync(session.NativeSessionId, Directory(session), cancellationToken).ConfigureAwait(false);
        if (native.Directory is not null) _directories[native.Id] = native.Directory;
        return ToSession(native);
    }

    public async Task<KernelSessionSummary> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.Session);
        var native = await Api().ForkSessionAsync(request.Session.NativeSessionId, request.NativeItemId, Directory(request.Session), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.Title)) native = await Api().UpdateSessionAsync(native.Id, request.Title, null, native.Directory, cancellationToken).ConfigureAwait(false);
        if (native.Directory is not null) _directories[native.Id] = native.Directory;
        return ToSession(native);
    }

    public async Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
    {
        Validate(session);
        await Api().UpdateSessionAsync(session.NativeSessionId, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Directory(session), cancellationToken).ConfigureAwait(false);
    }

    public async Task<KernelTurn> StartTurnAsync(SessionRef session, TurnInput input, TurnOptions options, CancellationToken cancellationToken = default)
    {
        Validate(session);
        var messageId = "msg_" + Guid.NewGuid().ToString("N");
        var parts = ToPromptParts(input);
        var model = ParseModel(options.ModelId);
        await Api().SendPromptAsync(session.NativeSessionId, messageId, parts, model?.ProviderId, model?.ModelId,
            options.AgentId, Directory(session), cancellationToken).ConfigureAwait(false);
        return new KernelTurn(session, messageId, TurnStatus.Running, DateTimeOffset.UtcNow);
    }

    public Task SteerTurnAsync(SessionRef session, string nativeTurnId, TurnInput input, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenCode Server 1.18.9 does not expose an explicit active-turn steering endpoint.");

    public async Task CancelTurnAsync(SessionRef session, string nativeTurnId, CancellationToken cancellationToken = default)
    {
        Validate(session);
        if (!await Api().AbortAsync(session.NativeSessionId, Directory(session), cancellationToken).ConfigureAwait(false))
            throw new OpenCodeAdapterException("OpenCode did not abort the session turn.");
    }

    public async Task RespondToPermissionAsync(PermissionResponse response, CancellationToken cancellationToken = default)
    {
        if (response.AmendedInput is not null) throw new NotSupportedException("OpenCode permission replies do not accept amended tool input.");
        var reply = response.ChoiceId switch { "once" => "once", "always" => "always", "reject" => "reject", _ => throw new ArgumentException("Unknown OpenCode permission choice.", nameof(response)) };
        _permissionDirectories.TryGetValue(response.PermissionId, out var directory);
        await Api().ReplyPermissionAsync(response.PermissionId, reply, null, directory, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondToElicitationAsync(ElicitationResponse response, CancellationToken cancellationToken = default)
    {
        _questionDirectories.TryGetValue(response.RequestId, out var directory);
        if (response.Cancelled)
            await Api().RejectQuestionAsync(response.RequestId, directory, cancellationToken).ConfigureAwait(false);
        else if (response.Value is JsonElement value)
            await Api().ReplyQuestionAsync(response.RequestId, value, directory, cancellationToken).ConfigureAwait(false);
        else
            throw new ArgumentException("A non-cancelled OpenCode question response requires a value.", nameof(response));
    }

    public async Task<ResultPage<KernelItem>> ReadHistoryAsync(SessionRef session, PageRequest request, CancellationToken cancellationToken = default)
    {
        Validate(session);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var messages = await Api().GetMessagesAsync(session.NativeSessionId, Directory(session), size, request.Cursor, cancellationToken).ConfigureAwait(false);
        var items = messages.SelectMany(ToItems).ToArray();
        var next = messages.Count == size ? OpenCodeEventNormalizer.String(messages[^1].Info, "id") : null;
        return new ResultPage<KernelItem>(items, next, messages.Count == size);
    }

    public async Task<KernelDiff?> ReadDiffAsync(SessionRef session, string? nativeTurnOrItemId = null, CancellationToken cancellationToken = default)
    {
        Validate(session);
        var diffs = await Api().GetDiffAsync(session.NativeSessionId, nativeTurnOrItemId, Directory(session), cancellationToken).ConfigureAwait(false);
        if (diffs.Count == 0) return null;
        return new KernelDiff(session, string.Join(Environment.NewLine, diffs.Select(x => x.Patch ?? string.Empty)),
            diffs.Select(x => x.File).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), false);
    }

    public async IAsyncEnumerable<KernelEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sse = _sse ?? throw new InvalidOperationException("OpenCode adapter is not ready.");
        var normalizer = _normalizer ?? throw new InvalidOperationException("OpenCode adapter is not initialized.");
        await foreach (var source in sse.WatchAsync(null, cancellationToken).ConfigureAwait(false))
        {
            TrackRequest(source);
            foreach (var item in normalizer.Normalize(source)) yield return item;
        }
    }

    public async Task<KernelSessionSummary> UpdateSessionTitleAsync(SessionRef session, string title, CancellationToken cancellationToken = default)
        => ToSession(await Api().UpdateSessionAsync(session.NativeSessionId, title, null, Directory(session), cancellationToken).ConfigureAwait(false));

    public Task<bool> DeleteSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
        => Api().DeleteSessionAsync(session.NativeSessionId, Directory(session), cancellationToken);

    public async Task<IReadOnlyList<KernelSessionSummary>> GetChildrenAsync(SessionRef session, CancellationToken cancellationToken = default)
        => (await Api().GetChildrenAsync(session.NativeSessionId, Directory(session), cancellationToken).ConfigureAwait(false)).Select(ToSession).ToArray();

    public Task<OpenCodeSession> RevertAsync(SessionRef session, string messageId, string? partId = null, CancellationToken cancellationToken = default)
        => Api().RevertAsync(session.NativeSessionId, messageId, partId, Directory(session), cancellationToken);

    public Task<OpenCodeSession> UnrevertAsync(SessionRef session, CancellationToken cancellationToken = default)
        => Api().UnrevertAsync(session.NativeSessionId, Directory(session), cancellationToken);

    public async Task<OpenCodeServerMetadata> GetServerMetadataAsync(string? directory, CancellationToken cancellationToken = default)
        => new(await Api().GetMcpStatusAsync(directory, cancellationToken).ConfigureAwait(false), await Api().GetAgentsAsync(directory, cancellationToken).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        if (_connection is not null)
        {
            await _connection.Process.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            await _connection.Process.DisposeAsync().ConfigureAwait(false);
        }
        _availability = KernelAvailability.Stopped;
    }

    private OpenCodeApiClient Api() => _api ?? throw new InvalidOperationException("OpenCode adapter is not ready.");
    private string? Directory(SessionRef session) => _directories.TryGetValue(session.NativeSessionId, out var value) ? value : null;
    private KernelSessionSummary ToSession(OpenCodeSession value)
    {
        if (value.Directory is not null) _directories[value.Id] = value.Directory;
        return new KernelSessionSummary(new SessionRef(Id, ProfileId(), value.Id, value.ParentId, value.WorkspaceId), value.Title ?? value.Id,
            value.Time.Archived is null ? SessionStatus.Idle : SessionStatus.Archived,
            DateTimeOffset.FromUnixTimeMilliseconds(value.Time.Created), DateTimeOffset.FromUnixTimeMilliseconds(value.Time.Updated), null,
            new Dictionary<string, string> { ["directory"] = value.Directory ?? string.Empty });
    }

    private IEnumerable<KernelItem> ToItems(OpenCodeMessageEnvelope envelope)
    {
        var role = OpenCodeEventNormalizer.String(envelope.Info, "role");
        var created = OpenCodeEventNormalizer.Element(envelope.Info, "time") is JsonElement time && OpenCodeEventNormalizer.Element(time, "created") is JsonElement value && value.TryGetInt64(out var millis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : DateTimeOffset.UtcNow;
        foreach (var part in envelope.Parts)
        {
            var type = OpenCodeEventNormalizer.String(part, "type");
            var id = OpenCodeEventNormalizer.String(part, "id") ?? OpenCodeEventNormalizer.String(envelope.Info, "id") ?? "unknown";
            ContentBlock content = type switch
            {
                "text" => new TextContentBlock(OpenCodeEventNormalizer.String(part, "text") ?? string.Empty),
                "reasoning" => new ReasoningContentBlock(OpenCodeEventNormalizer.String(part, "text") ?? string.Empty),
                "file" => new FileChangeContentBlock(OpenCodeEventNormalizer.String(part, "filename") ?? OpenCodeEventNormalizer.String(part, "url") ?? "unknown", "referenced"),
                _ => new NoticeContentBlock(type ?? "OpenCode part", OpenCodeEventNormalizer.Json(part), DiagnosticSeverity.Trace)
            };
            var kind = type switch { "reasoning" => KernelItemKind.Reasoning, "file" => KernelItemKind.FileChange, _ => role == "user" ? KernelItemKind.UserMessage : KernelItemKind.AssistantMessage };
            yield return new KernelItem(id, kind, KernelItemStatus.Completed, type, new[] { content }, created, created, VendorData: part.Clone());
        }
    }

    private static IReadOnlyList<object> ToPromptParts(TurnInput input)
    {
        var parts = new List<object>();
        foreach (var content in input.Content)
        {
            parts.Add(content switch
            {
                TextContentBlock text => new { type = "text", text = text.Text } as object,
                ReasoningContentBlock reasoning => new { type = "text", text = reasoning.Text },
                ImageContentBlock image => new { type = "file", mime = image.MediaType ?? "application/octet-stream", url = image.Uri, filename = image.AltText },
                FileChangeContentBlock file => new { type = "file", mime = "application/octet-stream", url = new Uri(Path.GetFullPath(file.Path)).AbsoluteUri, filename = Path.GetFileName(file.Path) },
                _ => new { type = "text", text = JsonSerializer.Serialize(content, content.GetType()) }
            });
        }
        if (input.ReferencedPaths is not null)
            foreach (var path in input.ReferencedPaths)
                parts.Add(new { type = "file", mime = "application/octet-stream", url = new Uri(Path.GetFullPath(path)).AbsoluteUri, filename = Path.GetFileName(path) });
        if (parts.Count == 0) throw new ArgumentException("A turn must contain at least one prompt part.", nameof(input));
        return parts;
    }

    private static (string ProviderId, string ModelId)? ParseModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1) throw new ArgumentException("OpenCode model IDs must use provider/model format.", nameof(model));
        return (model[..separator], model[(separator + 1)..]);
    }

    private void TrackRequest(OpenCodeSseEvent source)
    {
        var properties = OpenCodeEventNormalizer.Element(source.VendorJson, "properties");
        if (properties is not JsonElement value) return;
        var id = OpenCodeEventNormalizer.String(value, "id");
        if (id is null) return;
        var directory = OpenCodeEventNormalizer.String(value, "directory");
        var type = OpenCodeEventNormalizer.String(source.VendorJson, "type");
        if (type?.StartsWith("permission", StringComparison.Ordinal) == true) _permissionDirectories[id] = directory ?? string.Empty;
        if (type?.StartsWith("question", StringComparison.Ordinal) == true) _questionDirectories[id] = directory ?? string.Empty;
    }

    private void Validate(SessionRef session)
    {
        if (session.AdapterId != Id || session.ProfileId != ProfileId()) throw new ArgumentException("Session does not belong to this OpenCode adapter profile.", nameof(session));
    }

    private string ProfileId() => _context?.Profile.ProfileId ?? throw new InvalidOperationException("OpenCode adapter is not initialized.");
    private SecurityDescriptor Security() => new(SecurityEnforcementKind.PermissionGate, "OpenCode",
        _directories.Values.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        NetworkRestricted: false, ProcessRestricted: false, IsVerifiedByHost: false,
        "OpenCode enforces tool permissions. Talah Harness does not claim OS sandbox, network, or process isolation.");

    private static KernelHealth Health(KernelAvailability availability, string summary, string code, DiagnosticSeverity severity, string? remediation = null)
        => new(availability, summary, DateTimeOffset.UtcNow, new[] { new KernelDiagnostic(code, severity, summary, remediation) });

    private sealed record PendingLogin(string ProviderId, int MethodIndex, string? Directory);
}

public sealed class OpenCodeAdapterFactory : IKernelAdapterFactory
{
    public string AdapterId => OpenCodeAdapter.Id;

    public ValueTask<IKernelAdapter> CreateAsync(KernelProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.AdapterId != AdapterId) throw new ArgumentException("Profile adapter ID does not match OpenCode.", nameof(profile));
        return ValueTask.FromResult<IKernelAdapter>(new OpenCodeAdapter());
    }
}
