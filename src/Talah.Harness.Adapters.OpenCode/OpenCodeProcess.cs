using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Talah.Harness.Runtime;

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
        string? candidate;
        if (environment.TryGetValue("OPENCODE_EXECUTABLE", out string? configured) && !string.IsNullOrWhiteSpace(configured))
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

        string? version = await ReadVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!Version.TryParse(version, out Version? parsed) ||
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
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(segment, fileName));
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
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureMinimalEnvironment(startInfo);
        startInfo.ArgumentList.Add("--version");
        using var job = new WindowsJobObject();
        WindowsJobProcess? jobProcess = null;
        try
        {
            jobProcess = WindowsJobProcess.Start(startInfo, job);
            Process process = jobProcess.Process;
            Task<string?> outputTask = ReadBoundedLineAsync(jobProcess.StandardOutput, 4_096, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? (await outputTask.ConfigureAwait(false))?.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            jobProcess?.Dispose();
        }
    }

    private static void ConfigureMinimalEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (string name in EnvironmentAllowList)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) startInfo.Environment[name] = value;
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        await foreach (BoundedLine? line in BoundedLineReader.ReadLinesAsync(reader, maximumCharacters, cancellationToken).ConfigureAwait(false))
        {
            if (line.IsTruncated) throw new InvalidDataException("OpenCode version output exceeded its framing limit.");
            return line.Text;
        }

        return null;
    }

    private static string[] EnvironmentAllowList { get; } =
    [
        "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA"
    ];
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
        psi.Environment.Clear();
        foreach (string argument in startInfo.Arguments) psi.ArgumentList.Add(argument);
        foreach (KeyValuePair<string, string> pair in startInfo.Environment) psi.Environment[pair.Key] = pair.Value;

        var job = new WindowsJobObject();
        WindowsJobProcess? jobProcess = null;
        try
        {
            jobProcess = WindowsJobProcess.Start(psi, job);
            return Task.FromResult<IOpenCodeProcessHandle>(new ProcessHandle(jobProcess, job));
        }
        catch
        {
            jobProcess?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private sealed class ProcessHandle : IOpenCodeProcessHandle
    {
        private readonly Process _process;
        private readonly WindowsJobProcess _jobProcess;
        private readonly WindowsJobObject _job;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<string> _standardOutput = CreateLineChannel();
        private readonly Channel<string> _standardError = CreateLineChannel();
        private readonly Task _standardOutputPump;
        private readonly Task _standardErrorPump;

        public ProcessHandle(WindowsJobProcess jobProcess, WindowsJobObject job)
        {
            _jobProcess = jobProcess;
            _process = jobProcess.Process;
            _job = job;
            _standardOutputPump = PumpAsync(jobProcess.StandardOutput, _standardOutput.Writer, "stdout", _lifetime.Token);
            _standardErrorPump = PumpAsync(jobProcess.StandardError, _standardError.Writer, "stderr", _lifetime.Token);
        }

        public bool HasExited => _process.HasExited;
        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken) =>
            ReadLineAsync(_standardOutput.Reader, cancellationToken);

        public Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken) =>
            ReadLineAsync(_standardError.Reader, cancellationToken);

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
                if (!_process.HasExited) _job.Terminate();
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process exited between checks.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            if (!_process.HasExited)
            {
                try
                {
                    _job.Terminate();
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }

            try
            {
                await Task.WhenAll(_standardOutputPump, _standardErrorPump).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            _job.Dispose();
            _jobProcess.Dispose();
            _lifetime.Dispose();
        }

        private static Channel<string> CreateLineChannel() => Channel.CreateBounded<string>(
            new BoundedChannelOptions(256)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });

        private static async Task PumpAsync(
            TextReader source,
            ChannelWriter<string> destination,
            string streamName,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (BoundedLine? line in BoundedLineReader.ReadLinesAsync(source, 65_536, cancellationToken).ConfigureAwait(false))
                {
                    if (line.IsTruncated)
                        throw new OpenCodeAdapterException($"OpenCode {streamName} line exceeded the 65536-character framing limit.");
                    destination.TryWrite(line.Text);
                }

                destination.TryComplete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                destination.TryComplete();
            }
            catch (Exception exception)
            {
                destination.TryComplete(exception);
            }
        }

        private static async Task<string?> ReadLineAsync(ChannelReader<string> lines, CancellationToken cancellationToken)
        {
            while (await lines.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (lines.TryRead(out string? line)) return line;
            }

            return null;
        }
    }
}

public sealed record OpenCodeServerConnection(
    Uri BaseUri,
    string Username,
    string Password,
    string NativeVersion,
    IOpenCodeProcessHandle Process);

public sealed class OpenCodeServerLauncher(
    IOpenCodeProcessSupervisor supervisor,
    HttpMessageHandler? readinessHandler = null,
    TimeSpan? startupTimeout = null)
{
    private static readonly Regex ListeningUrl = new(
        "https?://127\\.0\\.0\\.1:(?<port>[0-9]{1,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly IOpenCodeProcessSupervisor _supervisor = supervisor;
    private readonly HttpMessageHandler? _readinessHandler = readinessHandler;
    private readonly TimeSpan _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);

    public async Task<OpenCodeServerConnection> StartAsync(
        OpenCodeExecutableInfo executable,
        string dataRoot,
        IReadOnlyDictionary<string, string> profileEnvironment,
        CancellationToken cancellationToken)
    {
        if (executable.State == OpenCodeExecutableState.NotInstalled) throw new OpenCodeNotInstalledException(executable.Message);
        if (executable.State != OpenCodeExecutableState.Compatible) throw new OpenCodeIncompatibleVersionException(executable.Message);

        Directory.CreateDirectory(dataRoot);
        string username = "talah";
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Dictionary<string, string> environment = CreateProcessEnvironment(profileEnvironment);
        environment["OPENCODE_SERVER_USERNAME"] = username;
        environment["OPENCODE_SERVER_PASSWORD"] = password;
        environment["XDG_DATA_HOME"] = Path.Combine(dataRoot, "data");
        environment["XDG_CONFIG_HOME"] = Path.Combine(dataRoot, "config");
        environment["XDG_CACHE_HOME"] = Path.Combine(dataRoot, "cache");
        environment.Remove("OPENCODE_SERVER_PORT");

        string[] arguments = new[] { "serve", "--hostname", "127.0.0.1", "--port", "0", "--pure" };
        IOpenCodeProcessHandle process = await _supervisor.StartAsync(
            new OpenCodeProcessStartInfo(executable.Path, arguments, environment, dataRoot),
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(_startupTimeout);
            Uri baseUri = await ReadListeningUriAsync(process, startup.Token).ConfigureAwait(false);
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
            string? line = await process.ReadStandardOutputLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new OpenCodeAdapterException("OpenCode closed stdout before announcing its listening endpoint.");
            Match match = ListeningUrl.Match(line);
            if (!match.Success) continue;
            int port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
            if (port is < 1 or > IPEndPoint.MaxPort) throw new OpenCodeAdapterException("OpenCode announced an invalid loopback port.");
            return new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        }
    }

    private async Task WaitForReadinessAsync(Uri baseUri, string username, string password, CancellationToken cancellationToken)
    {
        using HttpClient client = _readinessHandler is null ? new HttpClient() : new HttpClient(_readinessHandler, disposeHandler: false);
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
                using HttpResponseMessage response = await client.GetAsync("global/health", cancellationToken).ConfigureAwait(false);
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

    private static Dictionary<string, string> CreateProcessEnvironment(IReadOnlyDictionary<string, string> profileEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in EnvironmentAllowList)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) environment[name] = value;
        }

        foreach (KeyValuePair<string, string> pair in profileEnvironment) environment[pair.Key] = pair.Value;
        return environment;
    }

    private static string[] EnvironmentAllowList { get; } =
    [
        "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "PATH", "PATHEXT",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"
    ];
}
