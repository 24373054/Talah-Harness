using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Talah.Harness.Adapters.OpenCode;

public sealed class OpenCodeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly int _maximumResponseBytes;

    public OpenCodeApiClient(
        Uri baseUri,
        string username,
        string password,
        HttpMessageHandler? handler = null,
        int maximumResponseBytes = 8 * 1024 * 1024)
    {
        if (!baseUri.IsLoopback) throw new ArgumentException("OpenCode Server must use a loopback URI.", nameof(baseUri));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 1024);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _maximumResponseBytes = maximumResponseBytes;
        _http.BaseAddress = baseUri;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUri => _http.BaseAddress!;

    public Task<OpenCodeHealth> GetHealthAsync(CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeHealth>(HttpMethod.Get, "global/health", null, cancellationToken);

    public Task<OpenCodeProviderList> ListProvidersAsync(string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeProviderList>(HttpMethod.Get, WithDirectory("provider", directory), null, cancellationToken);

    public Task<Dictionary<string, OpenCodeAuthMethod[]>> GetProviderAuthMethodsAsync(string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<Dictionary<string, OpenCodeAuthMethod[]>>(HttpMethod.Get, WithDirectory("provider/auth", directory), null, cancellationToken);

    public Task<OpenCodeOAuthAuthorization> BeginOAuthAsync(
        string providerId,
        int methodIndex,
        IReadOnlyDictionary<string, string>? inputs,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeOAuthAuthorization>(
            HttpMethod.Post,
            WithDirectory($"provider/{Escape(providerId)}/oauth/authorize", directory),
            new { method = methodIndex, inputs },
            cancellationToken);

    public Task<bool> CompleteOAuthAsync(
        string providerId,
        int methodIndex,
        string? code,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Post,
            WithDirectory($"provider/{Escape(providerId)}/oauth/callback", directory),
            new { method = methodIndex, code },
            cancellationToken);

    public Task<bool> SetApiKeyAsync(
        string providerId,
        string apiKey,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Put,
            $"auth/{Escape(providerId)}",
            new { type = "api", key = apiKey, metadata },
            cancellationToken);

    public Task<bool> RemoveAuthAsync(string providerId, CancellationToken cancellationToken)
        => SendJsonAsync<bool>(HttpMethod.Delete, $"auth/{Escape(providerId)}", null, cancellationToken);

    public Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(
        string? directory,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory)) query.Add($"directory={Uri.EscapeDataString(directory)}");
        if (limit > 0) query.Add($"limit={limit}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"start={Uri.EscapeDataString(cursor)}");
        string path = "session" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));
        return SendJsonAsync<IReadOnlyList<OpenCodeSession>>(HttpMethod.Get, path, null, cancellationToken);
    }

    public Task<OpenCodeSession> CreateSessionAsync(
        string directory,
        string? title,
        string? agent,
        (string ProviderId, string ModelId)? model,
        CancellationToken cancellationToken)
    {
        object? modelBody = model is null ? null : new { id = model.Value.ModelId, providerID = model.Value.ProviderId };
        return SendJsonAsync<OpenCodeSession>(
            HttpMethod.Post,
            WithDirectory("session", directory),
            new { title, agent, model = modelBody },
            cancellationToken);
    }

    public Task<OpenCodeSession> GetSessionAsync(string sessionId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeSession>(HttpMethod.Get, WithDirectory($"session/{Escape(sessionId)}", directory), null, cancellationToken);

    public Task<OpenCodeSession> UpdateSessionAsync(
        string sessionId,
        string? title,
        long? archived,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeSession>(
            HttpMethod.Patch,
            WithDirectory($"session/{Escape(sessionId)}", directory),
            new { title, time = archived is null ? null : new { archived } },
            cancellationToken);

    public Task<bool> DeleteSessionAsync(string sessionId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<bool>(HttpMethod.Delete, WithDirectory($"session/{Escape(sessionId)}", directory), null, cancellationToken);

    public Task<OpenCodeSession> ForkSessionAsync(
        string sessionId,
        string? messageId,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeSession>(
            HttpMethod.Post,
            WithDirectory($"session/{Escape(sessionId)}/fork", directory),
            new { messageID = messageId },
            cancellationToken);

    public Task<IReadOnlyList<OpenCodeSession>> GetChildrenAsync(string sessionId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<IReadOnlyList<OpenCodeSession>>(
            HttpMethod.Get,
            WithDirectory($"session/{Escape(sessionId)}/children", directory),
            null,
            cancellationToken);

    public Task<IReadOnlyList<OpenCodeMessageEnvelope>> GetMessagesAsync(
        string sessionId,
        string? directory,
        int limit,
        string? before,
        CancellationToken cancellationToken)
    {
        string path = WithDirectory($"session/{Escape(sessionId)}/message", directory);
        path += path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        path += $"limit={limit}";
        if (!string.IsNullOrWhiteSpace(before)) path += $"&before={Uri.EscapeDataString(before)}";
        return SendJsonAsync<IReadOnlyList<OpenCodeMessageEnvelope>>(HttpMethod.Get, path, null, cancellationToken);
    }

    public async Task SendPromptAsync(
        string sessionId,
        string messageId,
        IReadOnlyList<object> parts,
        string? providerId,
        string? modelId,
        string? agent,
        string? directory,
        CancellationToken cancellationToken)
    {
        object? model = providerId is null || modelId is null ? null : new { providerID = providerId, modelID = modelId };
        await SendNoContentAsync(
            HttpMethod.Post,
            WithDirectory($"session/{Escape(sessionId)}/prompt_async", directory),
            new { messageID = messageId, parts, model, agent },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AbortAsync(string sessionId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<bool>(HttpMethod.Post, WithDirectory($"session/{Escape(sessionId)}/abort", directory), new { }, cancellationToken);

    public Task<IReadOnlyList<OpenCodeFileDiff>> GetDiffAsync(
        string sessionId,
        string? messageId,
        string? directory,
        CancellationToken cancellationToken)
    {
        string path = WithDirectory($"session/{Escape(sessionId)}/diff", directory);
        if (!string.IsNullOrWhiteSpace(messageId)) path += (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + $"messageID={Escape(messageId)}";
        return SendJsonAsync<IReadOnlyList<OpenCodeFileDiff>>(HttpMethod.Get, path, null, cancellationToken);
    }

    public Task<OpenCodeSession> RevertAsync(
        string sessionId,
        string messageId,
        string? partId,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeSession>(HttpMethod.Post, WithDirectory($"session/{Escape(sessionId)}/revert", directory), new { messageID = messageId, partID = partId }, cancellationToken);

    public Task<OpenCodeSession> UnrevertAsync(string sessionId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<OpenCodeSession>(HttpMethod.Post, WithDirectory($"session/{Escape(sessionId)}/unrevert", directory), new { }, cancellationToken);

    public Task<bool> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        string response,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Post,
            WithDirectory($"session/{Escape(sessionId)}/permissions/{Escape(permissionId)}", directory),
            new { response },
            cancellationToken);

    public Task<bool> ReplyPermissionAsync(
        string permissionId,
        string reply,
        string? message,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Post,
            WithDirectory($"permission/{Escape(permissionId)}/reply", directory),
            new { reply, message },
            cancellationToken);

    public Task<bool> ReplyQuestionAsync(
        string requestId,
        JsonElement answers,
        string? directory,
        CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Post,
            WithDirectory($"question/{Escape(requestId)}/reply", directory),
            new { answers },
            cancellationToken);

    public Task<bool> RejectQuestionAsync(string requestId, string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<bool>(
            HttpMethod.Post,
            WithDirectory($"question/{Escape(requestId)}/reject", directory),
            new { },
            cancellationToken);

    public Task<Dictionary<string, JsonElement>> GetMcpStatusAsync(string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<Dictionary<string, JsonElement>>(HttpMethod.Get, WithDirectory("mcp", directory), null, cancellationToken);

    public Task<IReadOnlyList<JsonElement>> GetAgentsAsync(string? directory, CancellationToken cancellationToken)
        => SendJsonAsync<IReadOnlyList<JsonElement>>(HttpMethod.Get, WithDirectory("agent", directory), null, cancellationToken);

    public static HttpRequestMessage CreateEventRequest(string? directory, string? lastEventId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, WithDirectory("event", directory));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(lastEventId)) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        return request;
    }

    public Task<HttpResponseMessage> SendEventRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    internal async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await ReadBoundedAsync(content, _maximumResponseBytes, cancellationToken).ConfigureAwait(false));

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(response.Content, _maximumResponseBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpenCodeApiException((int)response.StatusCode, method.Method, path, Encoding.UTF8.GetString(bytes));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new OpenCodeAdapterException($"OpenCode API {method} {path} returned an empty JSON value.");
        }
        catch (JsonException ex)
        {
            throw new OpenCodeAdapterException($"OpenCode API {method} {path} returned invalid JSON.", ex);
        }
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        byte[] bytes = await ReadBoundedAsync(response.Content, _maximumResponseBytes, cancellationToken).ConfigureAwait(false);
        throw new OpenCodeApiException((int)response.StatusCode, method.Method, path, Encoding.UTF8.GetString(bytes));
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw new OpenCodePayloadTooLargeException(maximumBytes);

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new OpenCodePayloadTooLargeException(maximumBytes);
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string WithDirectory(string path, string? directory)
        => string.IsNullOrWhiteSpace(directory) ? path : $"{path}?directory={Uri.EscapeDataString(directory)}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
