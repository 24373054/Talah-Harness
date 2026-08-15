using System.Collections.Concurrent;
using System.Threading.Channels;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode;

internal sealed class OpenCodeEventBridge : IAsyncDisposable
{
    private readonly OpenCodeApiClient _api;
    private readonly OpenCodeEventNormalizer _normalizer;
    private readonly Action<OpenCodeSseEvent> _trackRequest;
    private readonly Channel<KernelEvent> _events = Channel.CreateBounded<KernelEvent>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<string, DirectoryWatch> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private int _completed;

    public OpenCodeEventBridge(
        OpenCodeApiClient api,
        OpenCodeEventNormalizer normalizer,
        Action<OpenCodeSseEvent> trackRequest)
    {
        _api = api;
        _normalizer = normalizer;
        _trackRequest = trackRequest;
    }

    public async Task StartDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (_watchers.ContainsKey(directory)) return;

        var watch = new DirectoryWatch();
        if (!_watchers.TryAdd(directory, watch)) return;

        var sse = new OpenCodeSseClient(_api);
        watch.Pump = Task.Run(() => PumpAsync(directory, sse, watch), CancellationToken.None);
        try
        {
            await watch.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _watchers.TryRemove(directory, out _);
            watch.Lifetime.Cancel();
            throw;
        }
    }

    public async Task WaitForDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (!_watchers.TryGetValue(directory, out DirectoryWatch? watch))
            throw new InvalidOperationException($"OpenCode event watch for directory '{directory}' has not been started.");
        await watch.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<KernelEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return;
        _lifetime.Cancel();
        foreach (DirectoryWatch watch in _watchers.Values) watch.Lifetime.Cancel();
        Task[] pumps = _watchers.Values.Select(watch => watch.Pump).Where(pump => pump is not null).Cast<Task>().ToArray();
        if (pumps.Length > 0)
        {
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        _events.Writer.TryComplete();
        _lifetime.Dispose();
    }

    private async Task PumpAsync(string directory, OpenCodeSseClient sse, DirectoryWatch watch)
    {
        try
        {
            await foreach (OpenCodeSseEvent source in sse.WatchAsync(directory, watch.Lifetime.Token).ConfigureAwait(false))
            {
                watch.Started.TrySetResult(true);
                _trackRequest(source);
                foreach (KernelEvent kernelEvent in _normalizer.Normalize(source))
                    await _events.Writer.WriteAsync(kernelEvent, watch.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (watch.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _events.Writer.TryComplete(exception);
        }
        finally
        {
            watch.Started.TrySetResult(true);
        }
    }

    private sealed class DirectoryWatch
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Lifetime { get; } = new();
        public Task? Pump { get; set; }
    }
}
