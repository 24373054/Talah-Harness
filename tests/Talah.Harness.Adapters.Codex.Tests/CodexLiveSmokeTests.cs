using System.Text.Json;

namespace Talah.Harness.Adapters.Codex.Tests;

public sealed class CodexLiveSmokeTests
{
    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task InstalledCliInitializeAccountAndModelsWithoutStartingTurn()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("TALAH_CODEX_LIVE_TEST"), "1", StringComparison.Ordinal),
            "Set TALAH_CODEX_LIVE_TEST=1 to enable the installed-CLI smoke test.");

        var transport = CodexProcessTransport.Start("codex", new Dictionary<string, string>());
        await using var client = new CodexAppServerClient(transport, TimeSpan.FromSeconds(30));
        var initialize = await client.InitializeAsync("1.0.0-live-smoke", default);
        Assert.True(initialize.TryGetProperty("userAgent", out _));

        var account = await client.RequestAsync("account/read", new { refreshToken = false });
        Assert.True(account.TryGetProperty("requiresOpenaiAuth", out _));
        Skip.If(
            !account.TryGetProperty("account", out var signedIn) || signedIn.ValueKind == JsonValueKind.Null,
            "Codex is signed out; initialize and account/read succeeded, but model/list requires credentials.");

        var models = await client.RequestAsync("model/list", new { limit = 1, includeHidden = false });
        Assert.True(models.TryGetProperty("data", out _));
    }
}
