using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Talah.Harness.Adapters.OpenCode;

public sealed record OpenCodeExecutableInfo(string Path, string? Version, OpenCodeExecutableState State, string Message);

public enum OpenCodeExecutableState
{
    NotInstalled,
    Compatible,
    Incompatible
}

public interface IOpenCodeExecutableDiscovery
{
    Task<OpenCodeExecutableInfo> DiscoverAsync(
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

public sealed class OpenCodeExecutableDiscovery : IOpenCodeExecutableDiscovery
{
    public async Task<OpenCodeExecutableInfo> DiscoverAsync(
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        string? candidate = null;
        if (environment.TryGetValue("OPENCODE_EXECUTABLE", out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            candidate = configured;
        }
        else
        {
            candidate = FindOnPath("opencode.exe") ?? FindOnPath("opencode");
        }

        if (candidate is null || !File.Exists(candidate))
        {
            return new OpenCodeExecutableInfo(
                candidate ?? "opencode.exe",
                null,
                OpenCodeExecutableState.NotInstalled,
                $"OpenCode {OpenCodeRelease.Version} is not installed. Configure OPENCODE_EXECUTABLE or install the pinned Windows x64 release.");
        }

        var version = await ReadVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!Version.TryParse(version, out var parsed) ||
            parsed < OpenCodeRelease.MinimumCompatibleVersion ||
            parsed >= OpenCodeRelease.MaximumCompatibleVersionExclusive)
        {
            return new OpenCodeExecutableInfo(
                candidate,
                version,
                OpenCodeExecutableState.Incompatible,
                $"OpenCode {version ?? "unknown"} is incompatible. Talah Harness requires >= {OpenCodeRelease.MinimumCompatibleVersion} and < {OpenCodeRelease.MaximumCompatibleVersionExclusive} (pinned {OpenCodeRelease.Version}).");
        }

        return new OpenCodeExecutableInfo(candidate, version, OpenCodeExecutableState.Compatible, "Compatible OpenCode executable found.");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(segment, fileName));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? (await outputTask.ConfigureAwait(false)).Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed record OpenCodeProcessStartInfo(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory);

public interface IOpenCodeProcessSupervisor
{
    Task<IOpenCodeProcessHandle> StartAsync(OpenCodeProcessStartInfo startInfo, CancellationToken cancellationToken);
}

public interface IOpenCodeProcessHandle : IAsyncDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken);
    Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken);
    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken);
}

public sealed class OpenCodeProcessSupervisor : IOpenCodeProcessSupervisor
{
    public Task<IOpenCodeProcessHandle> StartAsync(OpenCodeProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = startInfo.ExecutablePath,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in startInfo.Arguments) psi.ArgumentList.Add(argument);
        foreach (var pair in startInfo.Environment) psi.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new OpenCodeAdapterException("The OpenCode process could not be started.");
        return Task.FromResult<IOpenCodeProcessHandle>(new ProcessHandle(process));
    }

    private sealed class ProcessHandle : IOpenCodeProcessHandle
    {
        private readonly Process _process;

        public ProcessHandle(Process process) => _process = process;

        public bool HasExited => _process.HasExited;
        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
            => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken)
            => _process.StandardError.ReadLineAsync(cancellationToken).AsTask();

        public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
        {
            if (_process.HasExited) return;
            try
            {
                _process.CloseMainWindow();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(gracefulTimeout);
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process exited between checks.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }

            _process.Dispose();
        }
    }
}

public sealed record OpenCodeServerConnection(
    Uri BaseUri,
    string Username,
    string Password,
    string NativeVersion,
    IOpenCodeProcessHandle Process);

public sealed class OpenCodeServerLauncher
{
    private static readonly Regex ListeningUrl = new(
        "https?://127\\.0\\.0\\.1:(?<port>[0-9]{1,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly IOpenCodeProcessSupervisor _supervisor;
    private readonly HttpMessageHandler? _readinessHandler;
    private readonly TimeSpan _startupTimeout;

    public OpenCodeServerLauncher(
        IOpenCodeProcessSupervisor supervisor,
        HttpMessageHandler? readinessHandler = null,
        TimeSpan? startupTimeout = null)
    {
        _supervisor = supervisor;
        _readinessHandler = readinessHandler;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<OpenCodeServerConnection> StartAsync(
        OpenCodeExecutableInfo executable,
        string dataRoot,
        IReadOnlyDictionary<string, string> profileEnvironment,
        CancellationToken cancellationToken)
    {
        if (executable.State == OpenCodeExecutableState.NotInstalled) throw new OpenCodeNotInstalledException(executable.Message);
        if (executable.State != OpenCodeExecutableState.Compatible) throw new OpenCodeIncompatibleVersionException(executable.Message);

        Directory.CreateDirectory(dataRoot);
        var username = "talah";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var environment = new Dictionary<string, string>(profileEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCODE_SERVER_USERNAME"] = username,
            ["OPENCODE_SERVER_PASSWORD"] = password,
            ["XDG_DATA_HOME"] = Path.Combine(dataRoot, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(dataRoot, "config"),
            ["XDG_CACHE_HOME"] = Path.Combine(dataRoot, "cache")
        };
        environment.Remove("OPENCODE_SERVER_PORT");

        var arguments = new[] { "serve", "--hostname", "127.0.0.1", "--port", "0", "--pure" };
        var process = await _supervisor.StartAsync(
            new OpenCodeProcessStartInfo(executable.Path, arguments, environment, dataRoot),
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(_startupTimeout);
            var baseUri = await ReadListeningUriAsync(process, startup.Token).ConfigureAwait(false);
            await WaitForReadinessAsync(baseUri, username, password, startup.Token).ConfigureAwait(false);
            return new OpenCodeServerConnection(baseUri, username, password, executable.Version!, process);
        }
        catch
        {
            await process.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Uri> ReadListeningUriAsync(IOpenCodeProcessHandle process, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (process.HasExited) throw new OpenCodeAdapterException($"OpenCode exited during startup with code {process.ExitCode}.");
            var line = await process.ReadStandardOutputLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new OpenCodeAdapterException("OpenCode closed stdout before announcing its listening endpoint.");
            var match = ListeningUrl.Match(line);
            if (!match.Success) continue;
            var port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
            if (port is < 1 or > IPEndPoint.MaxPort) throw new OpenCodeAdapterException("OpenCode announced an invalid loopback port.");
            return new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        }
    }

    private async Task WaitForReadinessAsync(Uri baseUri, string username, string password, CancellationToken cancellationToken)
    {
        using var client = _readinessHandler is null ? new HttpClient() : new HttpClient(_readinessHandler, disposeHandler: false);
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync("global/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Server is still binding.
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }
    }
}
