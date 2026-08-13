using System.Runtime.CompilerServices;
using System.Text;

namespace Talah.Harness.Runtime;

public sealed record BoundedText(string Text, bool IsTruncated, long DroppedCharacters);

public sealed class BoundedTextRingBuffer
{
    private readonly char[] _buffer;
    private readonly object _gate = new();
    private int _start;
    private int _length;
    private long _dropped;

    public BoundedTextRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new char[capacity];
    }

    public int Capacity => _buffer.Length;

    public void Append(ReadOnlySpan<char> value)
    {
        lock (_gate)
        {
            foreach (char character in value)
            {
                if (_length == _buffer.Length)
                {
                    _start = (_start + 1) % _buffer.Length;
                    _dropped++;
                    _length--;
                }

                _buffer[(_start + _length) % _buffer.Length] = character;
                _length++;
            }
        }
    }

    public BoundedText Snapshot()
    {
        lock (_gate)
        {
            char[] result = new char[_length];
            for (int index = 0; index < _length; index++)
            {
                result[index] = _buffer[(_start + index) % _buffer.Length];
            }

            return new BoundedText(new string(result), _dropped != 0, _dropped);
        }
    }
}

public sealed record BoundedLine(string Text, bool IsTruncated, long DroppedCharacters);

public static class BoundedLineReader
{
    public static async IAsyncEnumerable<BoundedLine> ReadLinesAsync(
        TextReader reader,
        int maximumLineLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineLength, 1);

        char[] buffer = new char[Math.Min(4096, maximumLineLength)];
        var line = new StringBuilder(Math.Min(maximumLineLength, 4096));
        long dropped = 0;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length != 0 || dropped != 0)
                {
                    yield return new BoundedLine(line.ToString(), dropped != 0, dropped);
                }

                yield break;
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }

                    yield return new BoundedLine(line.ToString(), dropped != 0, dropped);
                    line.Clear();
                    dropped = 0;
                }
                else if (line.Length < maximumLineLength)
                {
                    line.Append(character);
                }
                else
                {
                    dropped++;
                }
            }
        }
    }
}
