using System.Diagnostics;
using System.Threading.Channels;
using Talah.Harness.Contracts;

namespace Talah.Harness.Runtime;

public sealed record ProcessLaunchOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool ProtocolStdout = true,
    int MaximumMessageLength = 1_048_576,
    int ProtocolQueueCapacity = 256,
    int StandardErrorCapacity = 65_536,
    ProcessTimeouts? Timeouts = null,
    string? GracefulShutdownInput = null);

public sealed record ProcessMessage(
    long Sequence,
    string Text,
    bool IsTruncated,
    long DroppedCharacters,
    long MessagesDroppedBefore);

public sealed record ProcessExitInformation(
    int ExitCode,
    DateTimeOffset ExitedAt,
    bool WasForced,
    bool WasCrash);

public sealed record ProcessRestartInformation(
    int Starts,
    int Restarts,
    ProcessExitInformation? LastExit);

public sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly ProcessLaunchOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly BoundedTextRingBuffer _standardError;
    private readonly Channel<ProcessMessage> _protocol;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private Process? _process;
    private WindowsJobProcess? _jobProcess;
    private WindowsJobObject? _job;
    private Task? _standardErrorPump;
    private Task? _standardOutputPump;
    private TaskCompletionSource<ProcessExitInformation> _exit = NewExitSource();
    private long _messageSequence;
    private long _droppedMessages;
    private int _starts;
    private bool _forceRequested;
    private bool _disposed;

    public ProcessSupervisor(ProcessLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Executable)) throw new ArgumentException("An executable is required.", nameof(options));
        if (!Path.IsPathFullyQualified(options.WorkingDirectory)) throw new ArgumentException("The working directory must be absolute.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMessageLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ProtocolQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.StandardErrorCapacity, 1);
        _options = options;
        _standardError = new BoundedTextRingBuffer(options.StandardErrorCapacity);
        _protocol = Channel.CreateBounded<ProcessMessage>(new BoundedChannelOptions(options.ProtocolQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
    }

    public int? ProcessId => IsRunning ? _process?.Id : null;
    public bool IsRunning => _process is { HasExited: false };
    public BoundedText StandardError => _standardError.Snapshot();
    public long ProtocolMessagesDropped => Interlocked.Read(ref _droppedMessages);
    public ProcessRestartInformation RestartInformation => new(_starts, Math.Max(0, _starts - 1), _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null);
    public IAsyncEnumerable<ProcessMessage> ReadProtocolMessagesAsync(CancellationToken cancellationToken = default) =>
        _protocol.Reader.ReadAllAsync(cancellationToken);

    public KernelHealth GetHealth()
    {
        bool running = IsRunning;
        ProcessExitInformation? lastExit = _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;
        var diagnostics = new List<KernelDiagnostic>();
        BoundedText stderr = StandardError;
        if (stderr.IsTruncated)
        {
            diagnostics.Add(new KernelDiagnostic("runtime.stderr.truncated", DiagnosticSeverity.Warning,
                $"Standard error was bounded; {stderr.DroppedCharacters} characters were discarded."));
        }

        long protocolDropped = ProtocolMessagesDropped;
        if (protocolDropped != 0)
        {
            diagnostics.Add(new KernelDiagnostic("runtime.protocol.messages-dropped", DiagnosticSeverity.Warning,
                $"The bounded protocol queue discarded {protocolDropped} messages."));
        }

        if (lastExit?.WasCrash == true)
        {
            diagnostics.Add(new KernelDiagnostic("runtime.process.crash", DiagnosticSeverity.Error,
                $"The sidecar exited unexpectedly with code {lastExit.ExitCode}."));
        }

        return new KernelHealth(
            running ? KernelAvailability.Ready : lastExit?.WasCrash == true ? KernelAvailability.Failed : KernelAvailability.Stopped,
            running ? $"Sidecar process {_process!.Id} is running." : lastExit is null ? "Sidecar has not started." : $"Sidecar exited with code {lastExit.ExitCode}.",
            DateTimeOffset.UtcNow,
            diagnostics);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) throw new InvalidOperationException("The sidecar is already running.");
            CleanupExitedProcess();
            Directory.CreateDirectory(_options.WorkingDirectory);
            _exit = NewExitSource();
            _forceRequested = false;
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.Executable,
                WorkingDirectory = _options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = _options.ProtocolStdout
            };
            startInfo.Environment.Clear();
            if (_options.Environment is not null)
            {
                foreach (KeyValuePair<string, string> pair in _options.Environment)
                {
                    ValidateEnvironmentVariable(pair.Key, pair.Value);
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            foreach (string argument in _options.Arguments) startInfo.ArgumentList.Add(argument);

            var job = new WindowsJobObject();
            WindowsJobProcess? jobProcess = null;
            try
            {
                jobProcess = WindowsJobProcess.Start(startInfo, job);
            }
            catch
            {
                jobProcess?.Dispose();
                job.Dispose();
                throw;
            }

            Process process = jobProcess.Process;
            _process = process;
            _jobProcess = jobProcess;
            _job = job;
            _starts++;
            _standardErrorPump = PumpStandardErrorAsync(jobProcess.StandardError, _disposeCancellation.Token);
            _standardOutputPump = _options.ProtocolStdout
                ? PumpStandardOutputAsync(jobProcess.StandardOutput, _disposeCancellation.Token)
                : DrainStandardOutputAsync(jobProcess.StandardOutput, _disposeCancellation.Token);
            _ = ObserveExitAsync(process);

            await Task.Yield();
            if (process.HasExited)
            {
                await _exit.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task WriteInputAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Process? process = _process;
        if (process is null || process.HasExited) throw new InvalidOperationException("The sidecar is not running.");
        StreamWriter input = _jobProcess?.StandardInput ?? throw new InvalidOperationException("The sidecar input stream is unavailable.");
        await input.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ProcessExitInformation> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public async Task<ProcessExitInformation?> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process = _process;
            if (process is null) return null;
            if (!process.HasExited)
            {
                if (_options.GracefulShutdownInput is not null)
                {
                    StreamWriter input = _jobProcess?.StandardInput ?? throw new InvalidOperationException("The sidecar input stream is unavailable.");
                    await input.WriteAsync(_options.GracefulShutdownInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                _jobProcess?.StandardInput.Close();
                TimeSpan timeout = (_options.Timeouts ?? ProcessTimeouts.Default).Shutdown;
                try
                {
                    await _exit.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _forceRequested = true;
                    _job?.Terminate();
                    await _exit.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
            }

            return await _exit.Task.ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _disposeCancellation.Cancel();
            _protocol.Writer.TryComplete();
            IEnumerable<Task> pumps = new[] { _standardErrorPump, _standardOutputPump }.Where(static task => task is not null).Cast<Task>();
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (TimeoutException) { }
            _job?.Dispose();
            _jobProcess?.Dispose();
            _jobProcess = null;
            _process = null;
            _disposeCancellation.Dispose();
            _lifecycle.Dispose();
        }
    }

    private async Task PumpStandardErrorAsync(TextReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                _standardError.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task PumpStandardOutputAsync(TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BoundedLine? line in BoundedLineReader.ReadLinesAsync(reader, _options.MaximumMessageLength, cancellationToken).ConfigureAwait(false))
            {
                var message = new ProcessMessage(
                    Interlocked.Increment(ref _messageSequence), line.Text, line.IsTruncated,
                    line.DroppedCharacters, Interlocked.Read(ref _droppedMessages));
                if (!_protocol.Writer.TryWrite(message)) Interlocked.Increment(ref _droppedMessages);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static async Task DrainStandardOutputAsync(TextReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4_096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task ObserveExitAsync(Process process)
    {
        try { await process.WaitForExitAsync(_disposeCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested) { return; }
        int exitCode;
        try { exitCode = process.ExitCode; } catch (InvalidOperationException) { return; }
        _exit.TrySetResult(new ProcessExitInformation(exitCode, DateTimeOffset.UtcNow, _forceRequested, !_forceRequested && exitCode != 0));
    }

    private void CleanupExitedProcess()
    {
        if (_process is null) return;
        _job?.Dispose();
        _job = null;
        _jobProcess?.Dispose();
        _jobProcess = null;
        _process = null;
    }

    private static TaskCompletionSource<ProcessExitInformation> NewExitSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateEnvironmentVariable(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Environment variable names must be non-empty and cannot contain '=' or NUL.");
        if (value.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException("Environment variable values cannot contain NUL.");
    }
}
