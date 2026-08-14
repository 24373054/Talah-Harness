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
        Assert.Equal(
            ["request_approval", "plan", "auto_approve", "bypass_permissions"],
            descriptor.Security.ApprovalPolicies!.Select(option => option.Value));
        Assert.Null(descriptor.Security.SandboxPolicies);
        Assert.Equal("request_approval", descriptor.Security.DefaultApprovalPolicy);
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

    [Theory]
    [InlineData("http://provider.example/v1")]
    [InlineData("https://user:password@provider.example/v1")]
    [InlineData("ftp://provider.example/v1")]
    public async Task ApiKey_RejectsUnsafeProviderEndpointsBeforeSecureRuntime(string endpoint)
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime();
        var (adapter, _) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                adapter.ConfigureApiKeyAsync(new ApiKeyCredential("openai", "must-not-leave", new Uri(endpoint))));
            Assert.Null(runtime.CapturedSecret);
            Assert.DoesNotContain("must-not-leave", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://provider.example/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:11434/v1")]
    public async Task ApiKey_AllowsHttpsAndLoopbackDevelopmentEndpoints(string endpoint)
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime();
        var (adapter, _) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            await adapter.ConfigureApiKeyAsync(
                new ApiKeyCredential("openai", "accepted-secret", new Uri(endpoint)));
            Assert.Equal("accepted-secret", runtime.CapturedSecret);
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

    [Theory]
    [InlineData("request_approval", false)]
    [InlineData("plan", false)]
    [InlineData("auto_approve", true)]
    [InlineData("bypass_permissions", true)]
    public async Task NativePermissionPolicyMapsExactlyToTlahRuntime(string mode, bool autoApprove)
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime();
        var (adapter, session) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            _ = await adapter.StartTurnAsync(
                session,
                new TurnInput([new TextContentBlock("policy")]),
                new TurnOptions(null, mode, null, null));
            _ = await WaitForEventAsync(adapter, KernelEventKind.TurnCompleted);

            Assert.Equal(mode, runtime.CapturedRunOptions!.PermissionMode);
            Assert.Equal(autoApprove, runtime.CapturedRunOptions.AutoApproveTools);
        }
    }

    [Theory]
    [InlineData("on-request", null)]
    [InlineData("request_approval", "workspace-write")]
    public async Task UnsupportedCrossKernelPolicyIsRejectedBeforeNativeRun(string approvalMode, string? sandboxMode)
    {
        using var temp = new TemporaryDirectory();
        var runtime = new FakeNativeRuntime();
        var (adapter, session) = await AdapterTestFactory.CreateAsync(runtime, temp.Path);
        await using (adapter)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.StartTurnAsync(
                session,
                new TurnInput([new TextContentBlock("policy")]),
                new TurnOptions(null, approvalMode, sandboxMode, null)));
            Assert.Equal(0, runtime.RunCallCount);
        }
    }

    [Theory]
    [InlineData("allow-once", true)]
    [InlineData("deny", false)]
    public async Task ApprovalCheckpoint_IsRecoveredAfterRestart_WithoutReplay(string choiceId, bool approved)
    {
        using var temp = new TemporaryDirectory();
        var state = new FakeNativeRuntimeState();
        var firstRuntime = new FakeNativeRuntime(state) { EmitApproval = true };
        var (firstAdapter, session) = await AdapterTestFactory.CreateAsync(firstRuntime, temp.Path);

        _ = await firstAdapter.StartTurnAsync(
            session,
            new TurnInput([new TextContentBlock("write")]),
            new TurnOptions(null, "request_approval", null, null));
        _ = await WaitForEventAsync(firstAdapter, KernelEventKind.PermissionRequested);
        await firstAdapter.DisposeAsync();

        var secondRuntime = new FakeNativeRuntime(state);
        await using var secondAdapter = await AdapterTestFactory.InitializeAsync(secondRuntime, temp.Path);
        KernelSessionSummary firstResume = await secondAdapter.ResumeSessionAsync(session);
        KernelSessionSummary repeatedResume = await secondAdapter.ResumeSessionAsync(session);

        Assert.Equal(SessionStatus.WaitingForApproval, firstResume.Status);
        Assert.Equal(SessionStatus.WaitingForApproval, repeatedResume.Status);
        KernelEvent requested = await WaitForEventAsync(secondAdapter, KernelEventKind.PermissionRequested);
        PermissionRequest permission = Assert.IsType<PermissionEventData>(requested.Data).Request;
        Assert.Equal(state.InvocationId.ToString("D"), permission.PermissionId);
        Assert.Equal(state.NativeTurnId.ToString("D"), permission.NativeTurnId);
        Assert.Equal(state.NativeTurnId.ToString("D"), requested.NativeTurnId);
        ResourceImpact impact = Assert.Single(permission.Impacts);
        Assert.Equal("file_write", impact.Target);
        Assert.Equal("write", impact.RiskLevel);
        Assert.Equal("{\"path\":\"a.txt\"}", impact.Detail);

        await secondAdapter.RespondToPermissionAsync(new PermissionResponse(permission.PermissionId, choiceId));
        IReadOnlyList<KernelEvent> completionEvents = await ReadUntilAsync(secondAdapter, KernelEventKind.TurnCompleted);

        Assert.DoesNotContain(completionEvents, item => item.Kind == KernelEventKind.PermissionRequested);
        Assert.Equal(approved, state.LastApprovalApproved);
        Assert.Equal(1, state.RunCallCount);
        Assert.Equal(1, state.ApprovalDecisionCount);
        Assert.Equal(1, state.ResumeCallCount);
        Assert.Equal(approved ? 1 : 0, state.DestructiveExecutionCount);

        KernelSessionSummary terminalResume = await secondAdapter.ResumeSessionAsync(session);
        Assert.Equal(SessionStatus.Completed, terminalResume.Status);
        Assert.Equal(1, state.RunCallCount);
        Assert.Equal(1, state.ResumeCallCount);
    }

    [Fact]
    public async Task TerminalCheckpoint_IsReconciledAfterRestart_WithoutResumingOrStartingRun()
    {
        using var temp = new TemporaryDirectory();
        var state = new FakeNativeRuntimeState();
        var firstRuntime = new FakeNativeRuntime(state);
        var (firstAdapter, session) = await AdapterTestFactory.CreateAsync(firstRuntime, temp.Path);

        _ = await firstAdapter.StartTurnAsync(
            session,
            new TurnInput([new TextContentBlock("finish")]),
            new TurnOptions(null, null, null, null));
        _ = await WaitForEventAsync(firstAdapter, KernelEventKind.TurnCompleted);
        await firstAdapter.DisposeAsync();

        var secondRuntime = new FakeNativeRuntime(state);
        await using var secondAdapter = await AdapterTestFactory.InitializeAsync(secondRuntime, temp.Path);
        KernelSessionSummary firstResume = await secondAdapter.ResumeSessionAsync(session);
        KernelSessionSummary repeatedResume = await secondAdapter.ResumeSessionAsync(session);

        Assert.Equal(SessionStatus.Completed, firstResume.Status);
        Assert.Equal(SessionStatus.Completed, repeatedResume.Status);
        Assert.Equal(1, state.RunCallCount);
        Assert.Equal(0, state.ResumeCallCount);
        Assert.Equal(0, state.ApprovalDecisionCount);
        Assert.Equal(0, state.DestructiveExecutionCount);
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
