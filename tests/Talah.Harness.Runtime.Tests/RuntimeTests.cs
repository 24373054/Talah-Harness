using System.Diagnostics;
using System.Text;
using Talah.Harness.Runtime;
using Xunit;

namespace Talah.Harness.Runtime.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void RingBuffer_IsBoundedAndReportsDroppedCharacters()
    {
        var buffer = new BoundedTextRingBuffer(5);
        buffer.Append("abcdef".AsSpan());
        var snapshot = buffer.Snapshot();
        Assert.Equal("bcdef", snapshot.Text);
        Assert.True(snapshot.IsTruncated);
        Assert.Equal(1, snapshot.DroppedCharacters);
    }

    [Fact]
    public async Task LineReader_TruncatesOneMessageWithoutGrowingUnbounded()
    {
        var lines = new List<BoundedLine>();
        await foreach (var line in BoundedLineReader.ReadLinesAsync(new StringReader("123456789\nok\n"), 4)) lines.Add(line);
        Assert.Collection(lines,
            line => { Assert.Equal("1234", line.Text); Assert.True(line.IsTruncated); Assert.Equal(5, line.DroppedCharacters); },
            line => { Assert.Equal("ok", line.Text); Assert.False(line.IsTruncated); });
    }

    [Fact]
    public async Task LineReader_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in BoundedLineReader.ReadLinesAsync(new BlockingReader(), 10, cancellation.Token)) { }
        });
    }

    [Fact]
    public void ProfilePaths_AreDeterministicAndRejectTraversal()
    {
        using var temp = new TemporaryDirectory();
        var paths = new ProfilePathProvider(temp.Path, hardenAcl: false);
        var first = paths.GetProfileRoot("codex", "work");
        var second = paths.GetProfileRoot("codex", "work");
        Assert.Equal(first, second);
        Assert.StartsWith(temp.Path, first, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => paths.GetProfileRoot("..", "work"));
        Assert.Throws<ArgumentException>(() => paths.GetProfileRoot("codex/escape", "work"));
    }

    [Fact]
    public async Task Dpapi_RoundTripsWithoutPersistingPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var paths = new ProfilePathProvider(temp.Path, hardenAcl: false);
        var store = new DpapiCredentialStore(paths);
        const string secret = "sk-this-value-must-not-be-plain";
        await store.SetAsync("codex", "default", "openai", secret);
        Assert.Equal(secret, await store.GetAsync("codex", "default", "openai"));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.GetCredentialRoot("codex", "default"), "openai.dpapi"));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(await store.DeleteAsync("codex", "default", "openai"));
        Assert.Null(await store.GetAsync("codex", "default", "openai"));
    }

    [Fact]
    public async Task ProfileMetadataCrud_DoesNotContainCredentialSecret()
    {
        using var temp = new TemporaryDirectory();
        var paths = new ProfilePathProvider(temp.Path, hardenAcl: false);
        var metadata = new ProfileMetadataStore(paths);
        var now = DateTimeOffset.UtcNow;
        var profile = new ProfileMetadata("codex", "default", "Codex", true, now, now);
        await metadata.UpsertAsync(profile);
        Assert.Equal(profile, await metadata.GetAsync("codex", "default"));
        Assert.Single(await metadata.ListAsync("codex"));
        var json = await File.ReadAllTextAsync(Path.Combine(paths.GetProfileRoot("codex", "default"), "profile.json"));
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(await metadata.DeleteAsync("codex", "default"));
    }

    [Fact]
    public void Redactor_RemovesEverySecretFromDiagnostics()
    {
        var redactor = new SecretRedactor(["tiny", "a-long-secret"]);
        var diagnostic = redactor.Redact("token=a-long-secret other=tiny");
        Assert.Equal("token=[REDACTED] other=[REDACTED]", diagnostic);
        Assert.DoesNotContain("secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("tiny", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supervisor_UsesArgumentListAndBoundsStandardError()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var supervisor = new ProcessSupervisor(new ProcessLaunchOptions(
            PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write(('x' * 200)); [Console]::Out.WriteLine('space argument'); Start-Sleep -Milliseconds 200"],
            temp.Path,
            MinimalEnvironment(),
            StandardErrorCapacity: 32));
        await using (supervisor)
        {
            await supervisor.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var messages = supervisor.ReadProtocolMessagesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await messages.MoveNextAsync());
            Assert.Equal("space argument", messages.Current.Text);
            await supervisor.WaitForExitAsync(timeout.Token);
            var stderr = supervisor.StandardError;
            Assert.Equal(32, stderr.Text.Length);
            Assert.True(stderr.IsTruncated);
            Assert.Equal(168, stderr.DroppedCharacters);
        }
    }

    [Fact]
    public async Task SuspendedLauncherPreservesWindowsArgumentBoundaries()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        string[] expected = ["space value", "quote\"inside", "trailing\\"];
        string script = Path.Combine(temp.Path, "echo-arguments.ps1");
        await File.WriteAllTextAsync(script, "[Console]::Out.WriteLine(($args | ForEach-Object { '<' + $_ + '>' }) -join '|')");
        var supervisor = new ProcessSupervisor(new ProcessLaunchOptions(
            PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. expected],
            temp.Path,
            MinimalEnvironment()));
        await using (supervisor)
        {
            await supervisor.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var messages = supervisor.ReadProtocolMessagesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await messages.MoveNextAsync());
            Assert.Equal(string.Join('|', expected.Select(value => $"<{value}>")), messages.Current.Text);
            await supervisor.WaitForExitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Supervisor_ReportsBoundedProtocolQueueOverflow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var supervisor = new ProcessSupervisor(new ProcessLaunchOptions(
            PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 100; 1..20 | ForEach-Object { [Console]::Out.WriteLine($_) }; Start-Sleep -Milliseconds 100"],
            temp.Path,
            MinimalEnvironment(),
            ProtocolQueueCapacity: 1));
        await using (supervisor)
        {
            await supervisor.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await supervisor.WaitForExitAsync(timeout.Token);
            Assert.True(supervisor.ProtocolMessagesDropped > 0);
            Assert.Contains(supervisor.GetHealth().Diagnostics, diagnostic => diagnostic.Code == "runtime.protocol.messages-dropped");
        }
    }

    [Fact]
    public async Task DisposingSupervisor_KillsDescendantProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var shutdown = new ProcessTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(250));
        var script = $"$p=Start-Process -FilePath '{PingPath}' -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden -PassThru; [Console]::Out.WriteLine($p.Id); [Console]::Out.Flush(); Wait-Process -Id $p.Id";
        var supervisor = new ProcessSupervisor(new ProcessLaunchOptions(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", script], temp.Path, MinimalEnvironment(), Timeouts: shutdown));
        await supervisor.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var messages = supervisor.ReadProtocolMessagesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var childId = int.Parse(messages.Current.Text, System.Globalization.CultureInfo.InvariantCulture);
        using var child = Process.GetProcessById(childId);
        Assert.False(child.HasExited);
        await supervisor.DisposeAsync();
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task Supervisor_StartHonorsCancellation()
    {
        using var temp = new TemporaryDirectory();
        var supervisor = new ProcessSupervisor(new ProcessLaunchOptions(PowerShellPath, ["-NoProfile"], temp.Path));
        await using (supervisor)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.StartAsync(cancelled.Token));
        }
    }

    [Fact]
    public async Task PrioritizedEventBufferDropsOnlyLossyValuesAndPreservesCriticalOrder()
    {
        var buffer = new PrioritizedEventBuffer<string>(3, 2, value => value.StartsWith("delta", StringComparison.Ordinal));
        Assert.True(buffer.TryWrite("critical-1"));
        Assert.True(buffer.TryWrite("delta-1"));
        Assert.True(buffer.TryWrite("critical-2"));
        Assert.True(buffer.TryWrite("critical-3"));
        Assert.True(buffer.TryWrite("delta-dropped"));
        buffer.Complete();

        var values = new List<string>();
        await foreach (string value in buffer.ReadAllAsync()) values.Add(value);

        Assert.Equal(["critical-1", "critical-2", "critical-3"], values);
        Assert.Equal(2, buffer.DroppedCount);
    }

    [Fact]
    public async Task TimeoutPrimitive_IdentifiesOperation()
    {
        var timeouts = new ProcessTimeouts(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<ProcessOperationTimeoutException>(() => ProcessTimeout.WaitAsync(Task.Delay(500), ProcessOperation.Startup, timeouts));
        Assert.Equal(ProcessOperation.Startup, error.Operation);
    }

    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string PingPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");

    private static IReadOnlyDictionary<string, string> MinimalEnvironment() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
        ["TEMP"] = Path.GetTempPath(),
        ["TMP"] = Path.GetTempPath()
    };

    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "talah-runtime-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
