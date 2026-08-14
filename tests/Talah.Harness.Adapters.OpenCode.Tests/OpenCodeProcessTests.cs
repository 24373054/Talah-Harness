using System.Net;
using Talah.Harness.Adapters.OpenCode;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode.Tests;

public sealed class OpenCodeProcessTests
{
    [Fact]
    public async Task LaunchIsLoopbackEphemeralAndSecretOnlyInEnvironment()
    {
        var process = new FakeProcess("OpenCode server listening on http://127.0.0.1:43123");
        var supervisor = new FakeSupervisor(process);
        var handler = new RecordingHandler(_ => OpenCodeClientTests.Json("""{"healthy":true,"version":"1.18.9"}"""));
        var launcher = new OpenCodeServerLauncher(supervisor, handler, TimeSpan.FromSeconds(1));
        await using var connection = await launcher.StartAsync(Compatible(), Path.Combine(Path.GetTempPath(), "talah-opencode-test"), new Dictionary<string, string>(), default).ContinueWith<IOpenCodeProcessHandle>(task => task.Result.Process);
        var start = Assert.IsType<OpenCodeProcessStartInfo>(supervisor.StartInfo);
        Assert.Equal(new[] { "serve", "--hostname", "127.0.0.1", "--port", "0", "--pure" }, start.Arguments);
        Assert.DoesNotContain(start.Arguments, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.True(start.Environment.TryGetValue("OPENCODE_SERVER_PASSWORD", out var password));
        Assert.True(password!.Length >= 64);
        Assert.DoesNotContain(password, string.Join(' ', start.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupTimeoutStopsAndDisposesWholeProcessBoundary()
    {
        var process = new FakeProcess("OpenCode server listening on http://127.0.0.1:43123");
        var supervisor = new FakeSupervisor(process);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var launcher = new OpenCodeServerLauncher(supervisor, handler, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync(Compatible(), Path.Combine(Path.GetTempPath(), "talah-opencode-timeout"), new Dictionary<string, string>(), default));
        Assert.True(process.Stopped);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AdapterReportsNotInstalledAndIncompatibleWithoutLaunching()
    {
        var notInstalled = new OpenCodeAdapter(new StubDiscovery(new OpenCodeExecutableInfo("opencode.exe", null, OpenCodeExecutableState.NotInstalled, "missing")), new FakeSupervisor(new FakeProcess(null)));
        await notInstalled.InitializeAsync(Context());
        Assert.Equal(KernelAvailability.NotInstalled, (await notInstalled.GetHealthAsync()).Availability);

        var incompatible = new OpenCodeAdapter(new StubDiscovery(new OpenCodeExecutableInfo("opencode.exe", "2.0.0", OpenCodeExecutableState.Incompatible, "bad version")), new FakeSupervisor(new FakeProcess(null)));
        await incompatible.InitializeAsync(Context());
        var health = await incompatible.GetHealthAsync();
        Assert.Equal(KernelAvailability.Failed, health.Availability);
        Assert.Contains("Incompatible", health.Diagnostics[0].Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerCrashIsReportedWithoutExposingLaunchSecret()
    {
        var process = new FakeProcess("OpenCode server listening on http://127.0.0.1:43123");
        var handler = new RecordingHandler(_ => OpenCodeClientTests.Json("""{"healthy":true,"version":"1.18.9"}"""));
        var adapter = new OpenCodeAdapter(new StubDiscovery(Compatible()), new FakeSupervisor(process), handler, TimeSpan.FromSeconds(1));
        await adapter.InitializeAsync(Context());
        process.Exited = true;
        var health = await adapter.GetHealthAsync();
        Assert.Equal(KernelAvailability.Failed, health.Availability);
        Assert.DoesNotContain("OPENCODE_SERVER_PASSWORD", string.Join(' ', health.Diagnostics.Select(x => x.Message)), StringComparison.Ordinal);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ApiKeyRejectsPlaintextRemoteProviderBeforeSendingCredential()
    {
        var process = new FakeProcess("OpenCode server listening on http://127.0.0.1:43123");
        var handler = new RecordingHandler(_ => OpenCodeClientTests.Json("""{"healthy":true,"version":"1.18.9"}"""));
        var adapter = new OpenCodeAdapter(new StubDiscovery(Compatible()), new FakeSupervisor(process), handler, TimeSpan.FromSeconds(1));
        await adapter.InitializeAsync(Context());
        int requestsBeforeCredential = handler.Requests.Count;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => adapter.ConfigureApiKeyAsync(
            new ApiKeyCredential("openai", "must-not-leave", new Uri("http://provider.example/v1"))));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.Equal(requestsBeforeCredential, handler.Requests.Count);
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData("on-request", null)]
    [InlineData(null, "workspace-write")]
    public async Task PerTurnCrossKernelPoliciesAreRejectedBeforeOpenCodeRequest(string? approvalMode, string? sandboxMode)
    {
        var process = new FakeProcess("OpenCode server listening on http://127.0.0.1:43123");
        var handler = new RecordingHandler(_ => OpenCodeClientTests.Json("""{"healthy":true,"version":"1.18.9"}"""));
        var adapter = new OpenCodeAdapter(new StubDiscovery(Compatible()), new FakeSupervisor(process), handler, TimeSpan.FromSeconds(1));
        await adapter.InitializeAsync(Context());
        int requestsBeforeTurn = handler.Requests.Count;
        var session = new SessionRef(OpenCodeAdapter.Id, "profile", "ses_policy");

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.StartTurnAsync(
            session,
            new TurnInput([new TextContentBlock("policy")]),
            new TurnOptions(null, approvalMode, sandboxMode, null)));

        Assert.Equal(requestsBeforeTurn, handler.Requests.Count);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task LivePinnedHealthAndCapturedSchemaSmoke_IsOptInAndNonBillable()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TALAH_OPENCODE_LIVE"), "1", StringComparison.Ordinal)) return;
        var discovery = new OpenCodeExecutableDiscovery();
        var info = await discovery.DiscoverAsync(new Dictionary<string, string>(), default);
        if (info.State != OpenCodeExecutableState.Compatible) return;
        Assert.Equal(OpenCodeRelease.Version, info.Version);
        var dataRoot = Path.Combine(Path.GetTempPath(), "TalahHarnessOpenCodeLive", Guid.NewGuid().ToString("N"));
        var launcher = new OpenCodeServerLauncher(new OpenCodeProcessSupervisor(), startupTimeout: TimeSpan.FromSeconds(20));
        var connection = await launcher.StartAsync(info, dataRoot, new Dictionary<string, string>(), default);
        try
        {
            using var api = new OpenCodeApiClient(connection.BaseUri, connection.Username, connection.Password);
            var health = await api.GetHealthAsync(default);
            Assert.True(health.Healthy);
            Assert.Equal(OpenCodeRelease.Version, health.Version);
        }
        finally
        {
            await connection.Process.StopAsync(TimeSpan.FromSeconds(2), default);
            await connection.Process.DisposeAsync();
        }
        var schema = FindSchema();
        Assert.True(File.Exists(schema));
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(schema));
        Assert.Equal("3.1.0", document.RootElement.GetProperty("openapi").GetString());
    }

    private static OpenCodeExecutableInfo Compatible() => new("opencode.exe", OpenCodeRelease.Version, OpenCodeExecutableState.Compatible, "ok");

    private static KernelInitializationContext Context() => new("1.0.0",
        new KernelProfile("profile", OpenCodeAdapter.Id, "OpenCode", Path.Combine(Path.GetTempPath(), "talah-opencode-profile"), new Dictionary<string, string>(), true),
        Path.GetTempPath(), Path.GetTempPath(), false);

    private static string FindSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "opencode", OpenCodeRelease.OpenApiSchemaFile);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return string.Empty;
    }
}

internal sealed class StubDiscovery(OpenCodeExecutableInfo info) : IOpenCodeExecutableDiscovery
{
    public Task<OpenCodeExecutableInfo> DiscoverAsync(IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        => Task.FromResult(info);
}

internal sealed class FakeSupervisor(FakeProcess process) : IOpenCodeProcessSupervisor
{
    public OpenCodeProcessStartInfo? StartInfo { get; private set; }
    public Task<IOpenCodeProcessHandle> StartAsync(OpenCodeProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        StartInfo = startInfo;
        return Task.FromResult<IOpenCodeProcessHandle>(process);
    }
}

internal sealed class FakeProcess(string? output) : IOpenCodeProcessHandle
{
    private readonly Queue<string?> _output = new(new[] { output });
    public bool Exited { get; set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasExited => Exited;
    public int? ExitCode => Exited ? 1 : null;
    public Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_output.Count == 0 ? null : _output.Dequeue());
    }
    public Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken) { Stopped = true; Exited = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
