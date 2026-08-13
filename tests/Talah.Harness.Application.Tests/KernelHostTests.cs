using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Talah.Harness.Application;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;
using Xunit;

namespace Talah.Harness.Application.Tests;

public sealed class KernelHostTests
{
    [Fact]
    public void WorkspacePolicyNormalizesAndRejectsEscapes()
    {
        using var fixture = new HostFixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Workspace, "src")).FullName;
        var descriptor = WorkspacePolicy.CreateDescriptor(fixture.Workspace);

        fixture.WorkspacePolicy.ValidateReferencedPaths(descriptor, [nested, @"src\future.cs"]);
        Assert.StartsWith("ws_", descriptor.WorkspaceId, StringComparison.Ordinal);
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.WorkspacePolicy.ValidateReferencedPaths(descriptor, [Path.Combine(fixture.Root, "outside.txt")]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.WorkspacePolicy.ValidateReferencedPaths(descriptor, [@"..\outside.txt"]));
    }

    [Fact]
    public async Task HostRejectsSensitiveProfileEnvironment()
    {
        await using var fixture = new HostFixture();
        await fixture.Host.InitializeAsync();
        var profile = fixture.Profile with
        {
            Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "must-not-leak" }
        };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.StartAsync(profile));
        Assert.DoesNotContain("must-not-leak", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAddsDurableWorkspaceBindingWhenAdapterOmitsIt()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var workspace = WorkspacePolicy.CreateDescriptor(fixture.Workspace, isTrusted: true);
        var result = await fixture.Host.CreateSessionAsync(fixture.Key, new CreateSessionRequest(workspace, "real session", "model", null));

        Assert.Equal(workspace.WorkspaceId, result.Session.WorkspaceId);
        var stored = await fixture.Repository.GetSessionAsync(result.Session);
        Assert.Equal(workspace.WorkspaceId, stored!.Summary.Session.WorkspaceId);
        Assert.Equal(Path.GetFullPath(fixture.Workspace).TrimEnd(Path.DirectorySeparatorChar),
            (await fixture.Repository.GetWorkspaceAsync(workspace.WorkspaceId))!.Workspace.RootPath);
    }

    [Fact]
    public async Task TurnRejectsReferencedPathOutsideDurableWorkspaceBeforeAdapterCall()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var session = await fixture.CreateSessionAsync();
        var input = new TurnInput([new TextContentBlock("inspect")], [Path.Combine(fixture.Root, "outside.txt")]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Host.StartTurnAsync(session.Session, input, new TurnOptions(null, null, null, null)));
        Assert.Equal(0, fixture.Adapter.StartTurnCalls);
    }

    [Fact]
    public async Task TurnRejectsUnsupportedOrUnboundedUserInputBeforeAdapterCall()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        var unsupported = new TurnInput([
            new NoticeContentBlock("not-user-input", "notice", DiagnosticSeverity.Information)
        ]);
        var oversized = new TurnInput([new TextContentBlock(new string('x', 8 * 1024 * 1024 + 1))]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Host.StartTurnAsync(session.Session, unsupported, new TurnOptions(null, null, null, null)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Host.StartTurnAsync(session.Session, oversized, new TurnOptions(null, null, null, null)));
        Assert.Equal(0, fixture.Adapter.StartTurnCalls);
    }

    [Fact]
    public async Task EventsAreDurableOrderedAndReplayableWithoutAHotSubscriber()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var session = await fixture.CreateSessionAsync();
        var first = fixture.Adapter.MakeEvent(session.Session, 1, KernelEventKind.TurnStarted,
            new TurnEventData(TurnStatus.Running), nativeTurnId: "turn-1");
        var second = fixture.Adapter.MakeEvent(session.Session, 2, KernelEventKind.ItemCompleted,
            new ItemEventData(fixture.Adapter.HistoryItem), nativeTurnId: "turn-1", nativeItemId: "item-1");
        await fixture.Adapter.EmitAsync(first);
        await fixture.Adapter.EmitAsync(second);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<DurableKernelEvent>();
        await foreach (var item in fixture.Host.WatchEventsAsync(cancellationToken: timeout.Token))
        {
            received.Add(item);
            if (received.Count == 3) break; // Adapter-ready plus the two emitted events.
        }

        Assert.Equal(received.OrderBy(item => item.HostSequence).Select(item => item.HostSequence), received.Select(item => item.HostSequence));
        Assert.Contains(received, item => item.Event.Kind == KernelEventKind.ItemCompleted);
        var replay = await fixture.Repository.GetEventsAfterAsync(0);
        Assert.Equal(received.Count, replay.Count);
        var stored = await fixture.Repository.GetSessionAsync(session.Session);
        var history = await fixture.Repository.GetHistoryAsync(stored!.HostSessionId, new PageRequest());
        Assert.Contains(history.Items, item => item.Item.NativeItemId == "item-1");
    }

    [Fact]
    public async Task PermissionIsPersistedBeforeResponseAndCannotBeResolvedTwice()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var session = await fixture.CreateSessionAsync();
        var request = new PermissionRequest(
            "permission-1", session.Session, "turn-1", "command", "Run command", "needed",
            [new ResourceImpact("process", fixture.Workspace, "execute", "high")],
            [new PermissionChoice("deny", PermissionDecisionKind.Deny, "Deny", null)]);
        await fixture.Adapter.EmitAsync(fixture.Adapter.MakeEvent(
            session.Session, 3, KernelEventKind.PermissionRequested, new PermissionEventData(request), nativeTurnId: "turn-1"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await fixture.Repository.ListPendingApprovalsAsync()).Count == 1, timeout.Token);
        var response = new PermissionResponse("permission-1", "deny");
        await fixture.Host.RespondToPermissionAsync(fixture.Key, response, timeout.Token);

        Assert.Equal(response, fixture.Adapter.PermissionResponse);
        Assert.Empty(await fixture.Repository.ListPendingApprovalsAsync(timeout.Token));
        Assert.Equal("resolved", (await fixture.Repository.GetApprovalAsync("permission-1", timeout.Token))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Host.RespondToPermissionAsync(fixture.Key, response, timeout.Token));
    }

    [Fact]
    public async Task FailedPermissionDeliveryIsMarkedIndeterminateAndCannotBeReplayedDestructively()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        var request = new PermissionRequest(
            "permission-failure", session.Session, "turn-1", "command", "Run command", null,
            [new ResourceImpact("process", fixture.Workspace, "execute", "high")],
            [new PermissionChoice("deny", PermissionDecisionKind.Deny, "Deny", null)]);
        await fixture.Adapter.EmitAsync(fixture.Adapter.MakeEvent(
            session.Session, 4, KernelEventKind.PermissionRequested, new PermissionEventData(request), nativeTurnId: "turn-1"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await fixture.Repository.ListPendingApprovalsAsync()).Count == 1, timeout.Token);
        fixture.Adapter.FailPermissionResponse = true;
        var response = new PermissionResponse("permission-failure", "deny");

        await Assert.ThrowsAsync<IOException>(() => fixture.Host.RespondToPermissionAsync(fixture.Key, response, timeout.Token));
        StoredApproval stored = (await fixture.Repository.GetApprovalAsync("permission-failure", timeout.Token))!;
        Assert.Equal("indeterminate", stored.Status);
        Assert.Equal("deny", stored.Response!.Value.GetProperty("choiceId").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Host.RespondToPermissionAsync(fixture.Key, response, timeout.Token));
    }

    [Fact]
    public async Task ElicitationIsDurableAndResolvedThroughTwoPhaseLedger()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        JsonElement schema = JsonSerializer.SerializeToElement(new { type = "string" });
        var request = new ElicitationRequest("question-1", session.Session, "Input", "Provide a value", schema);
        await fixture.Adapter.EmitAsync(fixture.Adapter.MakeEvent(
            session.Session, 5, KernelEventKind.ElicitationRequested, new ElicitationEventData(request), nativeTurnId: "turn-1"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await fixture.Host.GetPendingElicitationRequestsAsync(timeout.Token)).Count == 1, timeout.Token);
        JsonElement value = JsonSerializer.SerializeToElement("answer");
        var response = new ElicitationResponse("question-1", false, value);

        await fixture.Host.RespondToElicitationAsync(fixture.Key, response, timeout.Token);

        Assert.Equal(response, fixture.Adapter.ElicitationResponse);
        Assert.Empty(await fixture.Host.GetPendingElicitationRequestsAsync(timeout.Token));
        Assert.Equal("resolved", (await fixture.Repository.GetElicitationAsync("question-1", timeout.Token))!.Status);
    }

    [Fact]
    public async Task StableNativeEventIdentityDeduplicatesReconnectReplay()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        KernelEvent first = fixture.Adapter.MakeEvent(session.Session, 10, KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic("replay", DiagnosticSeverity.Information, "first")), nativeEventId: "vendor-42");
        KernelEvent replay = first with { Sequence = 99, Timestamp = first.Timestamp.AddSeconds(2) };
        await fixture.Adapter.EmitAsync(first);
        await fixture.Adapter.EmitAsync(replay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await fixture.Repository.GetEventsAfterAsync(0, 20)).Count >= 2, timeout.Token);

        IReadOnlyList<StoredCanonicalEvent> events = await fixture.Repository.GetEventsAfterAsync(0, 20, cancellationToken: timeout.Token);
        Assert.Single(events, item => item.Event.NativeEventId == "vendor-42");
    }

    [Fact]
    public async Task VendorPayloadSecretsAreRedactedBeforeDurablePersistence()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        const string apiKey = "sk-abcdefghijklmnopqrstuv";
        const string bearer = "Bearer credential-that-must-not-persist";
        JsonElement vendor = JsonSerializer.SerializeToElement(new
        {
            authorization = bearer,
            nested = new { api_key = apiKey, tokens = 12, note = bearer }
        });
        KernelEvent kernelEvent = fixture.Adapter.MakeEvent(
            session.Session,
            11,
            KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic(
                "vendor",
                DiagnosticSeverity.Information,
                "safe",
                VendorData: vendor)),
            nativeEventId: "redaction-event") with { VendorData = vendor };
        await fixture.Adapter.EmitAsync(kernelEvent);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await fixture.Repository.GetEventsAfterAsync(0, 20)).Any(item => item.Event.NativeEventId == "redaction-event"), timeout.Token);

        StoredCanonicalEvent stored = Assert.Single(
            await fixture.Repository.GetEventsAfterAsync(0, 20, cancellationToken: timeout.Token),
            item => item.Event.NativeEventId == "redaction-event");
        string serialized = JsonSerializer.Serialize(stored.Event);
        Assert.DoesNotContain(apiKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-that-must-not-persist", serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", serialized, StringComparison.Ordinal);
        Assert.Equal(12, stored.Event.VendorData!.Value.GetProperty("nested").GetProperty("tokens").GetInt32());
    }

    [Fact]
    public async Task HistoryAndArchiveAreProjectedIntoCanonicalStore()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var session = await fixture.CreateSessionAsync();
        var page = await fixture.Host.ReadHistoryAsync(session.Session, new PageRequest());
        Assert.Single(page.Items);
        var stored = await fixture.Repository.GetSessionAsync(session.Session);
        Assert.Single((await fixture.Repository.GetHistoryAsync(stored!.HostSessionId, new PageRequest())).Items);

        await fixture.Host.ArchiveSessionAsync(session.Session);
        Assert.True(fixture.Adapter.Archived);
        Assert.Equal(SessionStatus.Archived, (await fixture.Repository.GetSessionAsync(session.Session))!.Summary.Status);
    }

    [Fact]
    public async Task GracefulProfileStopDrainsTerminalEventsBeforeStoppingPump()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        KernelSessionSummary session = await fixture.CreateSessionAsync();
        KernelTurn turn = await fixture.Host.StartTurnAsync(
            session.Session,
            new TurnInput([new TextContentBlock("work")]),
            new TurnOptions(null, null, null, null));
        fixture.Adapter.EventOnDispose = fixture.Adapter.MakeEvent(
            session.Session,
            90,
            KernelEventKind.TurnCancelled,
            new TurnEventData(TurnStatus.Cancelled),
            nativeTurnId: turn.NativeTurnId,
            nativeEventId: "dispose-terminal");

        await fixture.Host.StopProfileAsync(fixture.Key);

        IReadOnlyList<StoredCanonicalEvent> events = await fixture.Repository.GetEventsAfterAsync(0, 20);
        Assert.Single(events, item => item.Event.NativeEventId == "dispose-terminal" && item.Event.Kind == KernelEventKind.TurnCancelled);
    }

    [Fact]
    public async Task EventFromWrongProfileStopsPumpAndDegradesHealthWithoutLeakingPayload()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var wrong = new KernelEvent("test", "other-profile", null, null, null, 99, DateTimeOffset.UtcNow,
            KernelEventKind.Diagnostic,
            new DiagnosticEventData(new KernelDiagnostic("bad", DiagnosticSeverity.Error, "secret-payload")));
        await fixture.Adapter.EmitAsync(wrong);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        HostedKernelSnapshot? snapshot = null;
        await WaitUntilAsync(async () =>
        {
            snapshot = (await fixture.Host.GetSnapshotsAsync(timeout.Token)).Single();
            return snapshot.EventPumpFailure is not null;
        }, timeout.Token);

        Assert.Equal(KernelAvailability.Degraded, snapshot!.Health.Availability);
        Assert.DoesNotContain("secret-payload", snapshot.EventPumpFailure!, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        while (!await predicate()) await Task.Delay(20, cancellationToken);
    }

    private sealed class HostFixture : IAsyncDisposable, IDisposable
    {
        public HostFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "talah-application-tests", Guid.NewGuid().ToString("N"));
            Workspace = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(Workspace);
            WorkspacePolicy = new WorkspacePolicy();
            Adapter = new ScriptedAdapter();
            var database = new HarnessDatabase(new HarnessDatabaseOptions(Path.Combine(Root, "harness.db")));
            Repository = new CanonicalRepository(database);
            Host = new KernelHost([new ScriptedFactory(Adapter)], database, Repository, WorkspacePolicy);
            Profile = new KernelProfile("default", "test", "Test", Path.Combine(Root, "profile"), new Dictionary<string, string>(), true);
        }

        public string Root { get; }
        public string Workspace { get; }
        public WorkspacePolicy WorkspacePolicy { get; }
        public ScriptedAdapter Adapter { get; }
        public CanonicalRepository Repository { get; }
        public KernelHost Host { get; }
        public KernelProfile Profile { get; }
        public KernelProfileKey Key => new("test", "default");

        public async Task<HostedKernelSnapshot> StartAsync(KernelProfile? profile = null)
        {
            if (profile is null) await Host.InitializeAsync();
            return await Host.StartProfileAsync(profile ?? Profile, "1.0.0", Root, Root);
        }

        public Task<KernelSessionSummary> CreateSessionAsync()
        {
            var workspace = WorkspacePolicy.CreateDescriptor(Workspace, isTrusted: true);
            return Host.CreateSessionAsync(Key, new CreateSessionRequest(workspace, "session", null, null));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Dispose();
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ScriptedFactory(ScriptedAdapter adapter) : IKernelAdapterFactory
    {
        public string AdapterId => "test";
        public ValueTask<IKernelAdapter> CreateAsync(KernelProfile profile, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IKernelAdapter>(adapter);
    }

    private sealed class ScriptedAdapter : IKernelAdapter
    {
        private readonly Channel<KernelEvent> _events = Channel.CreateUnbounded<KernelEvent>();
        private KernelProfile? _profile;
        private int _session;

        public string AdapterId => "test";
        public int StartTurnCalls { get; private set; }
        public bool Archived { get; private set; }
        public PermissionResponse? PermissionResponse { get; private set; }
        public ElicitationResponse? ElicitationResponse { get; private set; }
        public bool FailPermissionResponse { get; set; }
        public KernelEvent? EventOnDispose { get; set; }
        public KernelItem HistoryItem { get; } = new(
            "item-1", KernelItemKind.AssistantMessage, KernelItemStatus.Completed, "Answer",
            [new TextContentBlock("real mapped content")], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public KernelDescriptor Descriptor => new(
            AdapterId, "Test kernel", "test", "1.0.0", "1", KernelAvailability.Ready,
            new KernelCapabilities(true, true, true, true, true, true, true, true, true, false, true, true, false, false, false, false),
            new SecurityDescriptor(SecurityEnforcementKind.PermissionGate, "test", [], false, false, true, "test"));

        public ValueTask InitializeAsync(KernelInitializationContext context, CancellationToken cancellationToken = default)
        {
            _profile = context.Profile;
            return ValueTask.CompletedTask;
        }

        public Task<KernelHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new KernelHealth(KernelAvailability.Ready, "ready", DateTimeOffset.UtcNow, []));
        public Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticationState(AuthenticationStatus.SignedIn, "test", null, [AuthenticationMethod.ApiKey]));
        public Task<LoginChallenge> BeginLoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoginChallenge("login", request.Method, null, null, null, "test"));
        public Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureApiKeyAsync(ApiKeyCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KernelModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KernelModel>>([new KernelModel("model", "Model", null, true)]);
        public Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<KernelSessionSummary>([], null, false));

        public async Task<KernelSessionSummary> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var session = new KernelSessionSummary(
                new SessionRef(AdapterId, _profile!.ProfileId, $"session-{Interlocked.Increment(ref _session)}"),
                request.Title ?? "session", SessionStatus.Idle, now, now, null);
            await EmitAsync(MakeEvent(session.Session, 1, KernelEventKind.SessionCreated, new SessionEventData(session)));
            return session;
        }

        public Task<KernelSessionSummary> ResumeSessionAsync(SessionRef session, CancellationToken cancellationToken = default) =>
            Task.FromResult(Summary(session));
        public Task<KernelSessionSummary> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Summary(new SessionRef(AdapterId, _profile!.ProfileId, "fork", request.Session.NativeSessionId)));
        public Task ArchiveSessionAsync(SessionRef session, CancellationToken cancellationToken = default)
        {
            Archived = true;
            return Task.CompletedTask;
        }

        public Task<KernelTurn> StartTurnAsync(SessionRef session, TurnInput input, TurnOptions options, CancellationToken cancellationToken = default)
        {
            StartTurnCalls++;
            return Task.FromResult(new KernelTurn(session, "turn-1", TurnStatus.Running, DateTimeOffset.UtcNow));
        }

        public Task SteerTurnAsync(SessionRef session, string nativeTurnId, TurnInput input, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelTurnAsync(SessionRef session, string nativeTurnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToPermissionAsync(PermissionResponse response, CancellationToken cancellationToken = default)
        {
            if (FailPermissionResponse) throw new IOException("Simulated uncertain transport failure.");
            PermissionResponse = response;
            return Task.CompletedTask;
        }

        public Task RespondToElicitationAsync(ElicitationResponse response, CancellationToken cancellationToken = default)
        {
            ElicitationResponse = response;
            return Task.CompletedTask;
        }
        public Task<ResultPage<KernelItem>> ReadHistoryAsync(SessionRef session, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<KernelItem>([HistoryItem], null, false));
        public Task<KernelDiff?> ReadDiffAsync(SessionRef session, string? nativeTurnOrItemId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<KernelDiff?>(null);
        public async IAsyncEnumerable<KernelEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }

        public async ValueTask DisposeAsync()
        {
            if (EventOnDispose is not null) await _events.Writer.WriteAsync(EventOnDispose);
            _events.Writer.TryComplete();
        }

        public ValueTask EmitAsync(KernelEvent item) => _events.Writer.WriteAsync(item);

        public KernelEvent MakeEvent(
            SessionRef session,
            long sequence,
            KernelEventKind kind,
            KernelEventData data,
            string? nativeTurnId = null,
            string? nativeItemId = null,
            string? nativeEventId = null) =>
            new(AdapterId, _profile!.ProfileId, session.NativeSessionId, nativeTurnId, nativeItemId,
                sequence, DateTimeOffset.UtcNow, kind, data, JsonSerializer.SerializeToElement(new { source = "test" }), nativeEventId);

        private static KernelSessionSummary Summary(SessionRef session)
        {
            var now = DateTimeOffset.UtcNow;
            return new KernelSessionSummary(session, session.NativeSessionId, SessionStatus.Idle, now, now, null);
        }
    }
}
