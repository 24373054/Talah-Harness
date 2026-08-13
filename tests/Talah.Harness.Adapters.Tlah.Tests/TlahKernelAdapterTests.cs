using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Tlah.Tests;

public sealed class TlahKernelAdapterTests
{
    [Fact]
    public void Descriptor_IsHonestAboutNativeCapabilities()
    {
        var descriptor = new TlahKernelAdapter(new FakeNativeRuntime()).Descriptor;
        Assert.Equal("tlah", descriptor.AdapterId);
        Assert.Equal("native-dotnet", descriptor.ProtocolKind);
        Assert.True(descriptor.Capabilities.CanUseApiKey);
        Assert.True(descriptor.Capabilities.CanApproveTools);
        Assert.True(descriptor.Capabilities.CanAmendToolInput);
        Assert.False(descriptor.Capabilities.CanForkSessions);
        Assert.False(descriptor.Capabilities.CanSteerActiveTurn);
        Assert.False(descriptor.Capabilities.CanReturnDiffs);
        Assert.Equal(SecurityEnforcementKind.PermissionGate, descriptor.Security.EnforcementKind);
        Assert.False(descriptor.Security.IsVerifiedByHost);
    }

    [Fact]
    public async Task ApiKey_IsPassedOnlyToSecureRuntime_AndRedactedFromErrors()
    {
        using var temp = new TemporaryDirectory();
        const string secret = "sk-super-secret-value";
        var runtime = new FakeNativeRuntime { ConfigureException = new InvalidOperationException($"bad {secret}") };
        var (adapter, _) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.ConfigureApiKeyAsync(new ApiKeyCredential("openai", secret)));
            Assert.Equal(secret, runtime.CapturedSecret);
            Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sessions_History_Archive_AndPaging_MapNativeRecords()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime();
        var (adapter, session) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            await adapter.StartTurnAsync(session,
                new TurnInput([new TextContentBlock("hello")]), new TurnOptions(null, null, null, null));
            await WaitForEventAsync(adapter, KernelEventKind.TurnCompleted);

            var history = await adapter.ReadHistoryAsync(session, new PageRequest(1));
            Assert.Single(history.Items);
            Assert.True(history.HasMore);
            Assert.Equal(KernelItemKind.UserMessage, history.Items[0].Kind);
            var second = await adapter.ReadHistoryAsync(session, new PageRequest(1, history.NextCursor));
            Assert.Equal(KernelItemKind.AssistantMessage, Assert.Single(second.Items).Kind);

            await adapter.ArchiveSessionAsync(session);
            var listed = await adapter.ListSessionsAsync(new PageRequest());
            Assert.Equal(SessionStatus.Archived, Assert.Single(listed.Items).Status);
        }
    }

    [Fact]
    public async Task Events_AreOrdered_AndNativeDeltasAreMapped()
    {
        using var temp = new TemporaryDirectory();
        var (adapter, session) = await AdapterTestFactory.CreateAsync(new FakeNativeRuntime(), temp.Path);
        await using (adapter)
        {
            _ = await adapter.StartTurnAsync(session, new TurnInput([new TextContentBlock("hello")]), new TurnOptions(null, null, null, null));
            var events = await ReadUntilAsync(adapter, KernelEventKind.TurnCompleted);
            Assert.Equal(events.OrderBy(item => item.Sequence), events);
            Assert.Contains(events, item => item.Kind == KernelEventKind.TurnStarted);
            Assert.Contains(events, item => item.Kind == KernelEventKind.ContentDelta &&
                item.Data is ContentDeltaEventData delta && delta.Delta == "native ");
            Assert.Contains(events, item => item.Kind == KernelEventKind.UsageUpdated &&
                item.Data is UsageEventData usage && usage.InputTokens == 12 && usage.OutputTokens == 4);
            Assert.Equal(KernelEventKind.TurnCompleted, events[^1].Kind);
        }
    }

    [Fact]
    public async Task Approval_Response_UsesNativeInvocation_AndResumesRun()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime { EmitApproval = true };
        var (adapter, session) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            _ = await adapter.StartTurnAsync(session, new TurnInput([new TextContentBlock("write")]), new TurnOptions(null, "request_approval", null, null));
            var requested = await WaitForEventAsync(adapter, KernelEventKind.PermissionRequested);
            var permission = Assert.IsType<PermissionEventData>(requested.Data).Request;
            await adapter.RespondToPermissionAsync(new PermissionResponse(permission.PermissionId, "allow-once"));
            _ = await WaitForEventAsync(adapter, KernelEventKind.TurnCompleted);
            Assert.True(runtime.ApprovalSet);
        }
    }

    [Fact]
    public async Task Cancellation_Propagates_AndDisposalReleasesRuntime()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime { RunBlock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (adapter, session) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        var turn = await adapter.StartTurnAsync(session, new TurnInput([new TextContentBlock("wait")]), new TurnOptions(null, null, null, null));
        await Task.Delay(50);
        await adapter.CancelTurnAsync(session, turn.NativeTurnId);
        _ = await WaitForEventAsync(adapter, KernelEventKind.TurnCancelled);
        await adapter.DisposeAsync();
        Assert.True(runtime.Cancelled);
        Assert.True(runtime.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetHealthAsync());
    }

    [Fact]
    public async Task CallerCancellationAfterStartDoesNotOwnRunningTurnLifetime()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime { RunBlock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var adapter = new TlahKernelAdapter(runtime, TimeSpan.FromMilliseconds(250));
        var profile = new KernelProfile("test-profile", TlahKernelAdapter.Id, "Test", temp.Path, new Dictionary<string, string>(), true);
        await adapter.InitializeAsync(new KernelInitializationContext("1.0.0", profile, temp.Path, temp.Path, false));
        string workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        SessionRef session = (await adapter.CreateSessionAsync(new CreateSessionRequest(
            new WorkspaceDescriptor("workspace", workspace, [], true), "Test", null, null))).Session;
        using var caller = new CancellationTokenSource();

        KernelTurn turn = await adapter.StartTurnAsync(
            session,
            new TurnInput([new TextContentBlock("continue")]),
            new TurnOptions(null, null, null, null),
            caller.Token);
        caller.Cancel();
        await Task.Delay(100);

        Assert.False(runtime.RunCancellationObserved);
        await adapter.CancelTurnAsync(session, turn.NativeTurnId);
        _ = await WaitForEventAsync(adapter, KernelEventKind.TurnCancelled);
        Assert.True(runtime.RunCancellationObserved);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task DisposalIsBoundedWhenNativeRunIgnoresCancellation()
    {
        using var temp = new TemporaryDirectory();
        var blocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeNativeRuntime { RunBlock = blocker, IgnoreRunCancellation = true };
        var adapter = new TlahKernelAdapter(runtime, TimeSpan.FromMilliseconds(100));
        var profile = new KernelProfile("test-profile", TlahKernelAdapter.Id, "Test", temp.Path, new Dictionary<string, string>(), true);
        await adapter.InitializeAsync(new KernelInitializationContext("1.0.0", profile, temp.Path, temp.Path, false));
        string workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        SessionRef session = (await adapter.CreateSessionAsync(new CreateSessionRequest(
            new WorkspaceDescriptor("workspace", workspace, [], true), "Test", null, null))).Session;
        _ = await adapter.StartTurnAsync(session, new TurnInput([new TextContentBlock("wait")]), new TurnOptions(null, null, null, null));

        Task disposal = adapter.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(runtime.Disposed);
        blocker.TrySetResult(true);
    }

    [Fact]
    public async Task Workspace_MustBeTrusted_AndAdditionalRootsMustBeContained()
    {
        using var temp = new TemporaryDirectory();
        var adapter = new TlahKernelAdapter(new FakeNativeRuntime());
        var profile = new KernelProfile("p", "tlah", "P", temp.Path, new Dictionary<string, string>(), true);
        await adapter.InitializeAsync(new KernelInitializationContext("1", profile, temp.Path, temp.Path, false));
        await using (adapter)
        {
            string root = System.IO.Path.Combine(temp.Path, "root");
            string outside = System.IO.Path.Combine(temp.Path, "outside");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CreateSessionAsync(
                new CreateSessionRequest(new WorkspaceDescriptor("w", root, [], false), null, null, null)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CreateSessionAsync(
                new CreateSessionRequest(new WorkspaceDescriptor("w", root, [outside], true), null, null, null)));
        }
    }

    [Fact]
    public async Task UnsupportedOperations_AreTruthful()
    {
        using var temp = new TemporaryDirectory();
        var (adapter, session) = await AdapterTestFactory.CreateAsync(new FakeNativeRuntime(), temp.Path);
        await using (adapter)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.BeginLoginAsync(new LoginRequest(AuthenticationMethod.Browser)));
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ForkSessionAsync(new ForkSessionRequest(session)));
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SteerTurnAsync(session, "turn", new TurnInput([])));
            Assert.Null(await adapter.ReadDiffAsync(session));
        }
    }

    private static async Task<KernelEvent> WaitForEventAsync(TlahKernelAdapter adapter, KernelEventKind kind)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in adapter.WatchEventsAsync(cts.Token))
            if (item.Kind == kind)
                return item;
        throw new InvalidOperationException("Event stream ended.");
    }

    private static async Task<List<KernelEvent>> ReadUntilAsync(TlahKernelAdapter adapter, KernelEventKind kind)
    {
        var result = new List<KernelEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in adapter.WatchEventsAsync(cts.Token))
        {
            result.Add(item);
            if (item.Kind == kind)
                return result;
        }
        throw new InvalidOperationException("Event stream ended.");
    }
}
