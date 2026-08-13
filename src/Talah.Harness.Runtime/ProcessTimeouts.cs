namespace Talah.Harness.Runtime;

public enum ProcessOperation
{
    Startup,
    Initialization,
    Idle,
    Turn,
    Shutdown
}

public sealed record ProcessTimeouts(
    TimeSpan Startup,
    TimeSpan Initialization,
    TimeSpan Idle,
    TimeSpan Turn,
    TimeSpan Shutdown)
{
    public static ProcessTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5));

    public TimeSpan For(ProcessOperation operation) => operation switch
    {
        ProcessOperation.Startup => Startup,
        ProcessOperation.Initialization => Initialization,
        ProcessOperation.Idle => Idle,
        ProcessOperation.Turn => Turn,
        ProcessOperation.Shutdown => Shutdown,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}

public sealed class ProcessOperationTimeoutException : TimeoutException
{
    public ProcessOperationTimeoutException(ProcessOperation operation, TimeSpan timeout)
        : base($"The {operation.ToString().ToLowerInvariant()} operation exceeded its {timeout} timeout.")
    {
        Operation = operation;
        Timeout = timeout;
    }

    public ProcessOperation Operation { get; }
    public TimeSpan Timeout { get; }
}

public static class ProcessTimeout
{
    public static async Task WaitAsync(
        Task task,
        ProcessOperation operation,
        ProcessTimeouts timeouts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeouts);
        var timeout = timeouts.For(operation);
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ProcessOperationTimeoutException(operation, timeout) { Source = exception.Source };
        }
    }
}
