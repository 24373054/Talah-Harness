using System.Collections.Concurrent;
using System.Text.Json;

namespace Talah.Harness.Adapters.Codex.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task InitializeFramesOneJsonObjectPerLineAndOnlyOnce()
    {
        var transport = new FakeCodexTransport();
        transport.OnSent = message =>
        {
            if (message.TryGetProperty("id", out var id))
            {
                transport.Send(new { id = id.Clone(), result = TestSupport.DefaultResponse("initialize") });
            }

            return Task.CompletedTask;
        };
        await using var client = new CodexAppServerClient(transport);

        await Task.WhenAll(client.InitializeAsync("1.0", default), client.InitializeAsync("1.0", default));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var initialize = await transport.NextSentAsync(timeout.Token);
        var initialized = await transport.NextSentAsync(timeout.Token);
        Assert.Equal("initialize", initialize.GetProperty("method").GetString());
        Assert.False(initialize.TryGetProperty("jsonrpc", out _));
        Assert.Equal("initialized", initialized.GetProperty("method").GetString());
        Assert.False(initialized.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task ConcurrentResponsesAreCorrelatedWhenReturnedOutOfOrder()
    {
        var requests = new ConcurrentDictionary<string, JsonElement>();
        var transport = new FakeCodexTransport();
        transport.OnSent = message =>
        {
            requests[message.GetProperty("method").GetString()!] = message.GetProperty("id").Clone();
            if (requests.Count == 2)
            {
                transport.Send(new { id = requests["second"], result = new { value = 2 } });
                transport.Send(new { id = requests["first"], result = new { value = 1 } });
            }

            return Task.CompletedTask;
        };
        await using var client = new CodexAppServerClient(transport);

        var first = client.RequestAsync("first", new { });
        var second = client.RequestAsync("second", new { });

        Assert.Equal(1, (await first).GetProperty("value").GetInt32());
        Assert.Equal(2, (await second).GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task UnknownAndMalformedMessagesProduceDiagnosticsWithoutStoppingClient()
    {
        var diagnostics = new List<string>();
        var transport = new FakeCodexTransport();
        transport.OnSent = message =>
        {
            transport.Send(new { id = message.GetProperty("id").Clone(), result = new { ok = true } });
            return Task.CompletedTask;
        };
        await using var client = new CodexAppServerClient(transport);
        client.DiagnosticReceived += diagnostics.Add;
        transport.SendRaw("not-json");
        transport.Send(new { unexpected = true, extensionField = 42 });

        var result = await client.RequestAsync("still/works", new { unknownClientField = true });

        Assert.True(result.GetProperty("ok").GetBoolean());
        await Task.Delay(50);
        Assert.True(diagnostics.Count >= 2);
    }

    [Fact]
    public async Task NotificationsAreDeliveredInExactWireOrderEvenWhenAHandlerIsSlow()
    {
        const int notificationCount = 200;
        var transport = new FakeCodexTransport();
        await using var client = new CodexAppServerClient(transport);
        var received = new List<int>(notificationCount);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += async (_, parameters, _) =>
        {
            var sequence = parameters.GetProperty("sequence").GetInt32();
            if (sequence % 17 == 0)
            {
                await Task.Delay(5);
            }

            received.Add(sequence);
            if (received.Count == notificationCount)
            {
                completed.TrySetResult();
            }
        };

        for (var index = 0; index < notificationCount; index++)
        {
            transport.Send(new { method = "test/ordered", @params = new { sequence = index } });
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, notificationCount), received);
    }

    [Fact]
    public async Task NotificationHandlerFailuresAreRedactedDiagnosticsAndDoNotBecomeUnobservedTasks()
    {
        var transport = new FakeCodexTransport();
        await using var client = new CodexAppServerClient(transport);
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subsequent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticReceived += value =>
        {
            if (value.Contains("handler failed", StringComparison.OrdinalIgnoreCase))
            {
                diagnostic.TrySetResult(value);
            }
        };
        client.NotificationReceived += (method, _, _) =>
        {
            if (method == "test/failing")
            {
                throw new InvalidOperationException("Bearer notification-handler-secret");
            }

            subsequent.TrySetResult();
            return Task.CompletedTask;
        };

        transport.Send(new { method = "test/failing", @params = new { } });
        transport.Send(new { method = "test/subsequent", @params = new { } });

        var value = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await subsequent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("notification-handler-secret", value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTimeoutIsReportedAndLateResponseIsIgnored()
    {
        var transport = new FakeCodexTransport();
        await using var client = new CodexAppServerClient(transport, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("slow", new { }));
    }

    [Fact]
    public async Task StderrIsRedactedAndReportedAsDiagnostic()
    {
        var transport = new FakeCodexTransport();
        await using var client = new CodexAppServerClient(transport);
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticReceived += value => diagnostic.TrySetResult(value);

        transport.SendError("request failed: Bearer top-secret");

        var value = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("top-secret", value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessExitFaultsPendingRequests()
    {
        var transport = new FakeCodexTransport();
        await using var client = new CodexAppServerClient(transport, TimeSpan.FromSeconds(5));
        var pending = client.RequestAsync("pending", new { });

        transport.Exit();

        await Assert.ThrowsAsync<CodexProtocolException>(() => pending);
    }

    [Fact]
    public async Task OversizedIncomingAndOutgoingMessagesAreRejected()
    {
        var outgoingTransport = new FakeCodexTransport();
        await using (var outgoingClient = new CodexAppServerClient(outgoingTransport, maximumMessageBytes: 64))
        {
            await Assert.ThrowsAsync<CodexProtocolException>(() =>
                outgoingClient.RequestAsync("large", new { text = new string('x', 100) }));
        }

        var incomingTransport = new FakeCodexTransport();
        await using var incomingClient = new CodexAppServerClient(incomingTransport, TimeSpan.FromSeconds(2), 64);
        var pending = incomingClient.RequestAsync("incoming", new { });
        incomingTransport.SendRaw(new string('x', 65));
        await Assert.ThrowsAsync<CodexProtocolException>(() => pending);
    }

    [Theory]
    [InlineData("Bearer abcdef", "Bearer [REDACTED]")]
    [InlineData("sk-secret-value", "sk-[REDACTED]")]
    [InlineData("{\"apiKey\":\"secret\"}", "{\"apiKey\":\"[REDACTED]\"}")]
    public void RedactionRemovesCredentials(string input, string expected)
    {
        Assert.Equal(expected, CodexAppServerClient.Redact(input));
    }

    [Fact]
    public void VendorPayloadRedactionIsRecursiveAndPreservesUnknownFields()
    {
        var vendor = JsonSerializer.SerializeToElement(new
        {
            future = new { accessToken = "secret", value = 42 },
            apiKey = "also-secret"
        });

        var sanitized = VendorJson.Sanitize(vendor);

        Assert.Equal(42, sanitized.GetProperty("future").GetProperty("value").GetInt32());
        Assert.Equal("[REDACTED]", sanitized.GetProperty("future").GetProperty("accessToken").GetString());
        Assert.Equal("[REDACTED]", sanitized.GetProperty("apiKey").GetString());
    }
}
