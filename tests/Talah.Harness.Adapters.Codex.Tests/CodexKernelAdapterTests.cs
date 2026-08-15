using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Runtime;

namespace Talah.Harness.Adapters.Codex.Tests;

public sealed class CodexKernelAdapterTests
{
    [Fact]
    public async Task AuthenticationFlowsUsePinnedMethodsAndNeverExposeSecretInEvents()
    {
        var calls = new List<(string Method, JsonElement Parameters)>();
        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, parameters) =>
        {
            calls.Add((method, parameters.Clone()));
            return method switch
            {
                "account/read" => new { account = new { type = "chatgpt", email = "me@example.test", planType = "pro" }, requiresOpenaiAuth = true },
                "account/login/start" when parameters.GetProperty("type").GetString() == "chatgpt" =>
                    new { type = "chatgpt", loginId = "browser-1", authUrl = "https://auth.openai.com/" },
                "account/login/start" when parameters.GetProperty("type").GetString() == "chatgptDeviceCode" =>
                    new { type = "chatgptDeviceCode", loginId = "device-1", userCode = "ABCD", verificationUrl = "https://auth.openai.com/device" },
                _ => new { }
            };
        });
        await using var adapter = fixture.Adapter;

        var state = await adapter.GetAuthenticationStateAsync();
        var browser = await adapter.BeginLoginAsync(new LoginRequest(AuthenticationMethod.Browser));
        var device = await adapter.BeginLoginAsync(new LoginRequest(AuthenticationMethod.DeviceCode));
        await adapter.CancelLoginAsync(device.LoginId);
        await adapter.ConfigureApiKeyAsync(new ApiKeyCredential("openai", "sk-test-secret"));
        await adapter.LogoutAsync();

        Assert.Equal(AuthenticationStatus.SignedIn, state.Status);
        Assert.Equal("browser-1", browser.LoginId);
        Assert.Equal("ABCD", device.UserCode);
        Assert.Contains(calls, call => call.Method == "account/login/cancel");
        Assert.Contains(calls, call => call.Method == "account/logout");
        Assert.False(adapter.Descriptor.Capabilities.CanConfigureProviders);
        Assert.Equal(["on-request", "untrusted", "never"],
            adapter.Descriptor.Security.ApprovalPolicies!.Select(option => option.Value));
        Assert.Equal(["workspace-write", "read-only", "danger-full-access"],
            adapter.Descriptor.Security.SandboxPolicies!.Select(option => option.Value));
    }

    [Fact]
    public async Task DeepSeekApiKeyIsDpapiProtectedAndDoesNotUseOpenAiLogin()

    {

        var calls = new List<string>();

        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, _) =>

        {

            calls.Add(method);

            return new { };

        });

        await using var adapter = fixture.Adapter;

        const string secret = "sk-deepseek-test-secret";

        await adapter.ConfigureApiKeyAsync(new ApiKeyCredential("deepseek", secret));

        Assert.DoesNotContain(calls, call => call == "account/login/start");


        string credentialRoot = Path.Combine(Path.GetFullPath(TestSupport.Profile.DataRoot), ".credentials");

        string credentialPath = Path.Combine(credentialRoot, "deepseek-api-key.dpapi");

        Assert.True(File.Exists(credentialPath));

        string protectedText = await File.ReadAllTextAsync(credentialPath);

        Assert.DoesNotContain(secret, protectedText, StringComparison.Ordinal);

        await adapter.LogoutAsync();
        Assert.DoesNotContain(calls, call => call == "account/logout");



        DpapiCredentialStore credentials = new(credentialRoot);

        Assert.Null(await credentials.GetAsync("codex", "test-profile", "deepseek-api-key"));

    }



    [Fact]
    public async Task ModelsAndThreadLifecycleMapToHostContracts()
    {
        var calls = new List<string>();
        JsonElement? forkParameters = null;
        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, parameters) =>
        {
            calls.Add(method);
            if (method == "thread/fork") forkParameters = parameters.Clone();
            return method switch
            {
                "model/list" => new { data = new[] { new { id = "gpt-5.6", model = "gpt-5.6", displayName = "GPT-5.6", description = "Model", isDefault = true, defaultReasoningEffort = "high" } }, nextCursor = (string?)null },
                "thread/list" => new { data = new[] { TestSupport.Thread("t-list") }, nextCursor = (string?)null },
                "thread/start" => new { thread = TestSupport.Thread("t-new") },
                "thread/resume" => new { thread = TestSupport.Thread("t-new") },
                "thread/fork" => new { thread = TestSupport.Thread("t-fork") },
                "thread/items/list" => new { data = new[] { new { id = "m1", type = "agentMessage", text = "hello", status = "completed" } }, nextCursor = (string?)null },
                _ => new { }
            };
        });
        await using var adapter = fixture.Adapter;

        var models = await adapter.ListModelsAsync();
        var listed = await adapter.ListSessionsAsync(new PageRequest());
        var created = await adapter.CreateSessionAsync(new CreateSessionRequest(
            new WorkspaceDescriptor("w", "C:/work", Array.Empty<string>(), true), "Named", null, null));
        var resumed = await adapter.ResumeSessionAsync(created.Session);
        var forked = await adapter.ForkSessionAsync(new ForkSessionRequest(
            created.Session,
            new NativeForkPoint(ForkPointKind.Turn, "turn-7"),
            "Fork"));
        var history = await adapter.ReadHistoryAsync(created.Session, new PageRequest());
        await adapter.ArchiveSessionAsync(created.Session);

        Assert.Single(models);
        Assert.Single(listed.Items);
        Assert.Equal("Named", created.Title);
        Assert.Equal(created.Session.NativeSessionId, resumed.Session.NativeSessionId);
        Assert.Equal("t-fork", forked.Session.NativeSessionId);
        Assert.Equal("turn-7", forkParameters?.GetProperty("lastTurnId").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.ForkSessionAsync(new ForkSessionRequest(
            created.Session,
            new NativeForkPoint(ForkPointKind.Message, "message-7"))));
        Assert.IsType<TextContentBlock>(Assert.Single(history.Items).Content.Single());
        Assert.Contains("thread/archive", calls);
        Assert.Contains(Path.GetFullPath("C:/work"), adapter.Descriptor.Security.WritableRoots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnStartSteerInterruptAndStreamAreNormalizedWithVendorData()
    {
        var calls = new List<string>();
        JsonElement? startParameters = null;
        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, parameters) =>
        {
            calls.Add(method);
            if (method == "turn/start") startParameters = parameters.Clone();
            return method switch
            {
                "turn/start" => new { turn = new { id = "turn-1", status = "inProgress", items = Array.Empty<object>(), startedAt = 1_700_000_000_000L } },
                _ => new { }
            };
        });
        await using var adapter = fixture.Adapter;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var events = adapter.WatchEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var initialization = await TestSupport.NextEventAsync(events, timeout.Token);
        var session = new SessionRef("codex", "test-profile", "thread-1");

        var turn = await adapter.StartTurnAsync(
            session,
            new TurnInput(new ContentBlock[] { new TextContentBlock("hello") }),
            new TurnOptions(null, "on-request", "workspace-write", null));
        await adapter.SteerTurnAsync(session, turn.NativeTurnId,
            new TurnInput(new ContentBlock[] { new TextContentBlock("more") }));
        fixture.Transport.Send(new { method = "turn/started", @params = new { threadId = "thread-1", turn = new { id = "turn-1", status = "inProgress" }, extension = 1 } });
        fixture.Transport.Send(new { method = "item/agentMessage/delta", @params = new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", delta = "Hi", future = true } });
        fixture.Transport.Send(new { method = "turn/completed", @params = new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed", items = Array.Empty<object>() } } });
        await adapter.CancelTurnAsync(session, turn.NativeTurnId);

        var started = await TestSupport.NextEventAsync(events, timeout.Token);
        var delta = await TestSupport.NextEventAsync(events, timeout.Token);
        var completed = await TestSupport.NextEventAsync(events, timeout.Token);
        Assert.Equal(KernelEventKind.AdapterStatusChanged, initialization.Kind);
        Assert.Equal(KernelEventKind.TurnStarted, started.Kind);
        Assert.Equal("Hi", Assert.IsType<ContentDeltaEventData>(delta.Data).Delta);
        Assert.Equal(KernelEventKind.TurnCompleted, completed.Kind);
        Assert.NotNull(delta.VendorData);
        Assert.Equal("on-request", startParameters?.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspaceWrite", startParameters?.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.Contains("turn/steer", calls);
        Assert.Contains("turn/interrupt", calls);
    }

    [Theory]
    [InlineData("allow-once", "accept")]
    [InlineData("allow-session", "acceptForSession")]
    [InlineData("deny", "decline")]
    public async Task ApprovalResponsesCorrelateExactServerRequest(string choice, string expectedDecision)
    {
        var fixture = await TestSupport.CreateInitializedAdapterAsync();
        await using var adapter = fixture.Adapter;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var events = adapter.WatchEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await TestSupport.NextEventAsync(events, timeout.Token);
        fixture.Transport.Send(new
        {
            id = 91,
            method = "item/commandExecution/requestApproval",
            @params = new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                itemId = "item-1",
                startedAtMs = 1,
                command = "dotnet test",
                cwd = "C:/work",
                reason = "run tests"
            }
        });
        var requestEvent = await TestSupport.NextEventAsync(events, timeout.Token);
        var permission = Assert.IsType<PermissionEventData>(requestEvent.Data).Request;

        await adapter.RespondToPermissionAsync(new PermissionResponse(permission.PermissionId, choice));

        JsonElement response;
        do
        {
            response = await fixture.Transport.NextSentAsync(timeout.Token);
        }
        while (!response.TryGetProperty("result", out _));
        Assert.Equal(91, response.GetProperty("id").GetInt32());
        Assert.Equal(expectedDecision, response.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ElicitationAndUnknownNotificationsAreMappedAndSequenceIsMonotonic()
    {
        var fixture = await TestSupport.CreateInitializedAdapterAsync();
        await using var adapter = fixture.Adapter;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var events = adapter.WatchEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = await TestSupport.NextEventAsync(events, timeout.Token);
        fixture.Transport.Send(new
        {
            id = "question-1",
            method = "item/tool/requestUserInput",
            @params = new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                itemId = "item-1",
                isBlocking = true,
                questions = new[] { new { id = "q1", header = "Choice", question = "Pick one" } }
            }
        });
        fixture.Transport.Send(new { method = "future/notification", @params = new { threadId = "thread-1", value = 1 } });
        var next = await TestSupport.NextEventAsync(events, timeout.Token);
        var following = await TestSupport.NextEventAsync(events, timeout.Token);
        var elicitationEvent = next.Kind == KernelEventKind.ElicitationRequested ? next : following;
        var unknownEvent = next.Kind == KernelEventKind.Diagnostic ? next : following;
        var elicitation = Assert.IsType<ElicitationEventData>(elicitationEvent.Data).Request;
        var answer = JsonSerializer.SerializeToElement(new { q1 = new[] { "A" } });
        await adapter.RespondToElicitationAsync(new ElicitationResponse(elicitation.RequestId, false, answer));

        Assert.Equal(KernelEventKind.ElicitationRequested, elicitationEvent.Kind);
        Assert.Equal(KernelEventKind.Diagnostic, unknownEvent.Kind);
        Assert.True(first.Sequence < next.Sequence);
        Assert.True(next.Sequence < following.Sequence);
        Assert.Equal(3, new[] { first.Sequence, elicitationEvent.Sequence, unknownEvent.Sequence }.Distinct().Count());
    }

    [Fact]
    public async Task DiffNotificationAndThreadHistoryAreMapped()
    {
        const string diff = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new";
        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, _) => method switch
        {
            "thread/read" => new
            {
                thread = new
                {
                    turns = new[]
                    {
                        new
                        {
                            id = "turn-history",
                            items = new[]
                            {
                                new
                                {
                                    id = "file-1",
                                    type = "fileChange",
                                    status = "completed",
                                    changes = new[] { new { path = "a.txt", kind = "update", diff } }
                                }
                            }
                        }
                    }
                }
            },
            _ => new { }
        });
        await using var adapter = fixture.Adapter;
        var session = new SessionRef("codex", "test-profile", "thread-1");

        var historical = await adapter.ReadDiffAsync(session, "turn-history");
        fixture.Transport.Send(new
        {
            method = "turn/diff/updated",
            @params = new { threadId = "thread-1", turnId = "turn-live", diff }
        });
        await Task.Delay(50);
        var live = await adapter.ReadDiffAsync(session, "turn-live");

        Assert.Equal(diff, historical?.UnifiedDiff);
        Assert.Equal(diff, live?.UnifiedDiff);
        Assert.Contains("a.txt", Assert.IsType<KernelDiff>(live).ChangedPaths);
    }

    [Fact]
    public async Task InitializeIsIdempotentAndUnsupportedCapabilitiesAreTruthful()
    {
        var initializeCount = 0;
        var fixture = await TestSupport.CreateInitializedAdapterAsync((method, _) =>
        {
            if (method == "initialize")
            {
                initializeCount++;
                return TestSupport.DefaultResponse(method);
            }

            return new { };
        });
        await using var adapter = fixture.Adapter;

        await adapter.InitializeAsync(TestSupport.Context);

        Assert.Equal(1, initializeCount);
        Assert.False(adapter.Descriptor.Capabilities.CanAmendToolInput);
        Assert.False(adapter.Descriptor.Capabilities.CanConfigureMcp);
        Assert.False(adapter.Descriptor.Capabilities.CanReplayEvents);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.BeginLoginAsync(new LoginRequest(AuthenticationMethod.ExternalCli)));
    }
}
