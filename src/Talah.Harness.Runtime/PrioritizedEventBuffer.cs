using System.Runtime.CompilerServices;

namespace Talah.Harness.Runtime;

/// <summary>
/// A bounded, non-blocking event queue that may evict only caller-declared lossy values.
/// Critical values grow into a bounded emergency reserve instead of being silently discarded.
/// </summary>
public sealed class PrioritizedEventBuffer<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private readonly Func<T, bool> _isLossy;
    private readonly SemaphoreSlim _available = new(0);
    private readonly int _capacity;
    private readonly int _emergencyReserve;
    private long _dropped;
    private bool _completed;

    public PrioritizedEventBuffer(int capacity, int emergencyReserve, Func<T, bool> isLossy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(emergencyReserve);
        _capacity = capacity;
        _emergencyReserve = emergencyReserve;
        _isLossy = isLossy ?? throw new ArgumentNullException(nameof(isLossy));
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryWrite(T item)
    {
        lock (_gate)
        {
            if (_completed) return false;
            if (_items.Count < _capacity)
            {
                Enqueue(item);
                return true;
            }

            if (_isLossy(item))
            {
                Interlocked.Increment(ref _dropped);
                return true;
            }

            if (RemoveOldestLossy())
            {
                Interlocked.Increment(ref _dropped);
                Enqueue(item);
                return true;
            }

            if (_items.Count < _capacity + _emergencyReserve)
            {
                Enqueue(item);
                return true;
            }

            throw new EventBufferOverflowException(
                $"The critical event reserve of {_emergencyReserve} entries is exhausted.");
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _available.Release();
        }
    }

    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            T? item = default;
            bool hasItem;
            lock (_gate)
            {
                hasItem = _items.Count != 0;
                if (hasItem) item = _items.Dequeue();
                else if (_completed) yield break;
            }
            if (hasItem)
            {
                yield return item!;
                continue;
            }
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Enqueue(T item)
    {
        bool wasEmpty = _items.Count == 0;
        _items.Enqueue(item);
        if (wasEmpty) _available.Release();
    }

    private bool RemoveOldestLossy()
    {
        int count = _items.Count;
        bool removed = false;
        for (int index = 0; index < count; index++)
        {
            T item = _items.Dequeue();
            if (!removed && _isLossy(item))
            {
                removed = true;
                continue;
            }
            _items.Enqueue(item);
        }
        return removed;
    }
}

public sealed class EventBufferOverflowException(string message) : InvalidOperationException(message);
