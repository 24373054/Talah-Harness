using System.Text.Json;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Codex.Tests;

internal static class TestSupport
{
    public static KernelProfile Profile { get; } = new(
        "test-profile",
        "codex",
        "Test Codex",
        Path.GetTempPath(),
        new Dictionary<string, string>(),
        true);

    public static KernelInitializationContext Context { get; } = new(
        "1.0.0",
        Profile,
        Path.GetTempPath(),
        Path.GetTempPath(),
        false);

    public static async Task<(CodexKernelAdapter Adapter, FakeCodexTransport Transport)> CreateInitializedAdapterAsync(
        Func<string, JsonElement, object?>? responder = null)
    {
        var transport = new FakeCodexTransport();
        transport.OnSent = message =>
        {
            if (!message.TryGetProperty("method", out var methodElement) ||
                !message.TryGetProperty("id", out var id))
            {
                return Task.CompletedTask;
            }

            var method = methodElement.GetString()!;
            var parameters = message.TryGetProperty("params", out var value)
                ? value
                : JsonSerializer.SerializeToElement(new { });
            var response = responder?.Invoke(method, parameters) ?? DefaultResponse(method);
            transport.Send(new { id = id.Clone(), result = response });
            return Task.CompletedTask;
        };
        var client = new CodexAppServerClient(transport, TimeSpan.FromSeconds(2));
        var adapter = new CodexKernelAdapter(Profile, client);
        await adapter.InitializeAsync(Context);
        return (adapter, transport);
    }

    public static object DefaultResponse(string method) => method switch
    {
        "initialize" => new { codexHome = "C:/codex", platformFamily = "windows", platformOs = "windows", userAgent = "codex-cli/0.147.0" },
        _ => new { }
    };

    public static object Thread(string id, string preview = "preview") => new
    {
        id,
        sessionId = id,
        preview,
        name = (string?)null,
        cwd = "C:/work",
        cliVersion = "0.147.0",
        modelProvider = "openai",
        source = "appServer",
        status = new { type = "idle" },
        turns = Array.Empty<object>(),
        createdAt = 1_700_000_000L,
        updatedAt = 1_700_000_001L,
        ephemeral = false
    };

    public static async Task<KernelEvent> NextEventAsync(
        IAsyncEnumerator<KernelEvent> enumerator,
        CancellationToken cancellationToken = default)
    {
        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(cancellationToken);
        Assert.True(moved);
        return enumerator.Current;
    }
}
