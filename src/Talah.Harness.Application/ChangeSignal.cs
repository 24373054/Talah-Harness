namespace Talah.Harness.Application;

internal sealed class ChangeSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSource();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            previous = _changed;
            _changed = NewSource();
        }

        previous.TrySetResult();
    }

    public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _version != observedVersion
                ? Task.CompletedTask
                : _changed.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
