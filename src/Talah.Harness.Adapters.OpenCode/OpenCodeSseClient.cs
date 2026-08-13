using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Talah.Harness.Adapters.OpenCode;

public sealed class OpenCodeSseClient
{
    private readonly OpenCodeApiClient _api;
    private readonly int _maximumEventBytes;
    private readonly int _bufferCapacity;
    private readonly TimeSpan _minimumReconnectDelay;
    private readonly TimeSpan _maximumReconnectDelay;

    public OpenCodeSseClient(
        OpenCodeApiClient api,
        int maximumEventBytes = 1024 * 1024,
        int bufferCapacity = 256,
        TimeSpan? minimumReconnectDelay = null,
        TimeSpan? maximumReconnectDelay = null)
    {
        _api = api;
        _maximumEventBytes = maximumEventBytes > 0 ? maximumEventBytes : throw new ArgumentOutOfRangeException(nameof(maximumEventBytes));
        _bufferCapacity = bufferCapacity > 0 ? bufferCapacity : throw new ArgumentOutOfRangeException(nameof(bufferCapacity));
        _minimumReconnectDelay = minimumReconnectDelay ?? TimeSpan.FromMilliseconds(250);
        _maximumReconnectDelay = maximumReconnectDelay ?? TimeSpan.FromSeconds(5);
    }

    public async IAsyncEnumerable<OpenCodeSseEvent> WatchAsync(
        string? directory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<OpenCodeSseEvent>(new BoundedChannelOptions(_bufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        var producer = ProduceAsync(directory, channel.Writer, cancellationToken);
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        await producer.ConfigureAwait(false);
    }

    private async Task ProduceAsync(string? directory, ChannelWriter<OpenCodeSseEvent> writer, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenOrder = new Queue<string>();
        string? lastEventId = null;
        var reconnectDelay = _minimumReconnectDelay;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var request = _api.CreateEventRequest(directory, lastEventId);
                    using var response = await _api.SendEventRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        throw new OpenCodeApiException((int)response.StatusCode, "GET", "event", body);
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await foreach (var item in ParseAsync(stream, _maximumEventBytes, cancellationToken).ConfigureAwait(false))
                    {
                        lastEventId = item.Id ?? lastEventId;
                        var deduplicationId = item.Id ?? TryGetVendorEventId(item.VendorJson);
                        if (deduplicationId is not null && !seen.Add(deduplicationId)) continue;
                        if (deduplicationId is not null)
                        {
                            seenOrder.Enqueue(deduplicationId);
                            if (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
                        }

                        await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                    }

                    reconnectDelay = _minimumReconnectDelay;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OpenCodePayloadTooLargeException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OpenCodeApiException)
                {
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                    reconnectDelay = TimeSpan.FromMilliseconds(
                        Math.Min(Math.Max(_minimumReconnectDelay.TotalMilliseconds, reconnectDelay.TotalMilliseconds * 2), _maximumReconnectDelay.TotalMilliseconds));
                }
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    public static async IAsyncEnumerable<OpenCodeSseEvent> ParseAsync(
        Stream stream,
        int maximumEventBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? id = null;
        string? eventName = null;
        var data = new StringBuilder();
        var bytes = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0) yield return CreateEvent(id, eventName, data.ToString());
                yield break;
            }

            bytes = checked(bytes + Encoding.UTF8.GetByteCount(line) + 1);
            if (bytes > maximumEventBytes) throw new OpenCodePayloadTooLargeException(maximumEventBytes);

            if (line.Length == 0)
            {
                if (data.Length > 0) yield return CreateEvent(id, eventName, data.ToString());
                id = null;
                eventName = null;
                data.Clear();
                bytes = 0;
                continue;
            }

            if (line[0] == ':') continue;
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "id": id = value; break;
                case "event": eventName = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }

    private static OpenCodeSseEvent CreateEvent(string? id, string? eventName, string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return new OpenCodeSseEvent(id, eventName, data, document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new OpenCodeAdapterException("OpenCode SSE returned invalid JSON data.", ex);
        }
    }

    private static string? TryGetVendorEventId(JsonElement json)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
