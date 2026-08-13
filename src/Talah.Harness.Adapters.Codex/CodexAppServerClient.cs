using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Talah.Harness.Runtime;

namespace Talah.Harness.Adapters.Codex;

internal sealed record CodexServerRequest(JsonElement Id, string Method, JsonElement Parameters, JsonElement VendorData);

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    public const int DefaultMaximumMessageBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ICodexTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _serverRequestSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumMessageBytes;
    private readonly Task _receiveTask;
    private readonly Task _stderrTask;
    private readonly object _initializeLock = new();
    private Task<JsonElement>? _initializeTask;
    private long _nextRequestId;
    private int _disposed;

    public CodexAppServerClient(
        ICodexTransport transport,
        TimeSpan? requestTimeout = null,
        int maximumMessageBytes = DefaultMaximumMessageBytes,
        int maximumConcurrentServerRequests = 64)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentServerRequests);

        _transport = transport;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _maximumMessageBytes = maximumMessageBytes;
        _serverRequestSlots = new SemaphoreSlim(maximumConcurrentServerRequests, maximumConcurrentServerRequests);
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _stderrTask = Task.Run(StderrLoopAsync);
    }

    public event Func<string, JsonElement, JsonElement, Task>? NotificationReceived;

    public event Func<CodexServerRequest, Task>? ServerRequestReceived;

    public event Action<string>? DiagnosticReceived;

    public Task<JsonElement> InitializeAsync(string hostVersion, CancellationToken cancellationToken)
    {
        lock (_initializeLock)
        {
            return _initializeTask ??= InitializeCoreAsync(hostVersion, cancellationToken);
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        long id = Interlocked.Increment(ref _nextRequestId);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, source))
        {
            throw new InvalidOperationException("Duplicate Codex request identifier.");
        }

        try
        {
            using var timeout = new CancellationTokenSource(_requestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token,
                _lifetime.Token);
            try
            {
                await WriteAsync(new { id, method, @params = parameters ?? new { } }, linked.Token)
                    .ConfigureAwait(false);
                return await source.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Codex App Server request '{method}' timed out after {_requestTimeout}.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default) =>
        WriteAsync(new { method, @params = parameters ?? new { } }, cancellationToken);

    public Task RespondAsync(JsonElement id, object result, CancellationToken cancellationToken = default) =>
        WriteAsync(new { id, result }, cancellationToken);

    public Task RespondErrorAsync(
        JsonElement id,
        int code,
        string message,
        CancellationToken cancellationToken = default) =>
        WriteAsync(new { id, error = new { code, message } }, cancellationToken);

    private async Task<JsonElement> InitializeCoreAsync(string hostVersion, CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "talah-harness", title = "Talah Harness", version = hostVersion },
                capabilities = new
                {
                    experimentalApi = true,
                    mcpServerOpenaiFormElicitation = true
                }
            },
            cancellationToken).ConfigureAwait(false);
        await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > _maximumMessageBytes)
        {
            throw new CodexProtocolException(
                $"Outgoing Codex message is {bytes} bytes; limit is {_maximumMessageBytes} bytes.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.Input.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _transport.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (BoundedLine? framedLine in BoundedLineReader.ReadLinesAsync(
                               _transport.Output,
                               _maximumMessageBytes,
                               _lifetime.Token).ConfigureAwait(false))
            {
                if (framedLine.IsTruncated)
                {
                    throw new CodexProtocolException(
                        $"Incoming Codex message exceeded the {_maximumMessageBytes}-character framing limit; " +
                        $"{framedLine.DroppedCharacters} characters were discarded.");
                }

                string line = framedLine.Text;
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (bytes > _maximumMessageBytes)
                {
                    throw new CodexProtocolException(
                        $"Incoming Codex message is {bytes} bytes; limit is {_maximumMessageBytes} bytes.");
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    DiagnosticReceived?.Invoke($"Ignored malformed Codex message: {ex.Message}");
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        DiagnosticReceived?.Invoke("Ignored non-object Codex message.");
                        continue;
                    }

                    await DispatchAsync(root.Clone()).ConfigureAwait(false);
                }
            }

            await _transport.Completion.ConfigureAwait(false);
            failure = new CodexProtocolException("Codex App Server exited or closed stdout.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (failure is not null)
            {
                DiagnosticReceived?.Invoke(Redact(failure.Message));
                foreach (KeyValuePair<long, TaskCompletionSource<JsonElement>> pair in _pending)
                {
                    pair.Value.TrySetException(failure);
                }
            }
        }
    }

    private Task DispatchAsync(JsonElement root)
    {
        if (root.TryGetProperty("method", out JsonElement methodElement) &&
            methodElement.ValueKind == JsonValueKind.String)
        {
            string method = methodElement.GetString()!;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement.Clone()
                : EmptyObject();
            if (root.TryGetProperty("id", out JsonElement requestId))
            {
                Func<CodexServerRequest, Task>? handler = ServerRequestReceived;
                if (handler is null)
                {
                    return RespondErrorAsync(requestId.Clone(), -32601, $"Unsupported server request: {method}");
                }

                if (!_serverRequestSlots.Wait(0))
                {
                    return RespondErrorAsync(requestId.Clone(), -32001,
                        "The host is already handling the maximum number of concurrent server requests.",
                        _lifetime.Token);
                }

                _ = HandleServerRequestAsync(
                    handler,
                    new CodexServerRequest(requestId.Clone(), method, parameters, root.Clone()));
                return Task.CompletedTask;
            }

            return DispatchNotificationAsync(method, parameters, root.Clone());
        }

        if (root.TryGetProperty("id", out JsonElement idElement) &&
            TryGetInt64Id(idElement, out long id) &&
            _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? source))
        {
            if (root.TryGetProperty("error", out JsonElement error))
            {
                int code = error.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int parsed)
                    ? parsed
                    : -32603;
                string message = error.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? "Unknown error"
                    : "Unknown error";
                source.TrySetException(new CodexRpcException(code, Redact(message)));
            }
            else if (root.TryGetProperty("result", out JsonElement result))
            {
                source.TrySetResult(result.Clone());
            }
            else
            {
                source.TrySetException(new CodexProtocolException("Codex response has neither result nor error."));
            }

            return Task.CompletedTask;
        }

        DiagnosticReceived?.Invoke("Ignored unknown Codex message shape.");
        return Task.CompletedTask;
    }

    private async Task HandleServerRequestAsync(
        Func<CodexServerRequest, Task> handler,
        CodexServerRequest request)
    {
        try
        {
            await handler(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await RespondErrorAsync(request.Id, -32603, Redact(ex.Message), _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _serverRequestSlots.Release();
        }
    }

    private async Task DispatchNotificationAsync(string method, JsonElement parameters, JsonElement vendorData)
    {
        Func<string, JsonElement, JsonElement, Task>? handlers = NotificationReceived;
        if (handlers is null)
        {
            DiagnosticReceived?.Invoke($"Ignored Codex notification '{method}'.");
            return;
        }

        foreach (Func<string, JsonElement, JsonElement, Task> handler in handlers.GetInvocationList().Cast<Func<string, JsonElement, JsonElement, Task>>())
        {
            try
            {
                await handler(method, parameters, vendorData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticReceived?.Invoke(
                    Redact($"Codex notification handler failed for '{method}': {ex.Message}"));
            }
        }
    }

    private async Task StderrLoopAsync()
    {
        try
        {
            await foreach (BoundedLine? line in BoundedLineReader.ReadLinesAsync(
                               _transport.Error,
                               65_536,
                               _lifetime.Token).ConfigureAwait(false))
            {
                string suffix = line.IsTruncated
                    ? $" [stderr truncated; {line.DroppedCharacters} characters discarded]"
                    : string.Empty;
                DiagnosticReceived?.Invoke(Redact(line.Text) + suffix);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    internal static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string result = value;
        foreach (string? marker in new[] { "sk-", "Bearer ", "apiKey\":\"", "accessToken\":\"" })
        {
            int start = 0;
            while ((start = result.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int secretStart = start + marker.Length;
                int end = secretStart;
                while (end < result.Length && !char.IsWhiteSpace(result[end]) && result[end] != '"' && result[end] != ',')
                {
                    end++;
                }

                result = result[..secretStart] + "[REDACTED]" + result[end..];
                start = secretStart + "[REDACTED]".Length;
            }
        }

        return result;
    }

    private static bool TryGetInt64Id(JsonElement value, out long id)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out id);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out id);
        }

        id = default;
        return false;
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        foreach (KeyValuePair<long, TaskCompletionSource<JsonElement>> pair in _pending)
        {
            pair.Value.TrySetCanceled();
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_receiveTask, _stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _writeLock.Dispose();
        _serverRequestSlots.Dispose();
        _lifetime.Dispose();
    }
}
