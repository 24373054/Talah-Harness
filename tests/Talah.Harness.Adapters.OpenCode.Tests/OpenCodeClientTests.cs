using System.Net;
using System.Text;
using System.Text.Json;
using Talah.Harness.Adapters.OpenCode;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode.Tests;

public sealed class OpenCodeClientTests
{
    [Fact]
    public async Task HttpMapping_UsesBasicAuthAndTypedProviderModels()
    {
        var handler = new RecordingHandler(request => Json("""{"all":[{"id":"openai","name":"OpenAI","models":{"gpt":{"id":"gpt","name":"GPT"}}}],"default":{"openai":"gpt"},"connected":["openai"]}"""));
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "user", "secret", handler);
        var providers = await api.ListProvidersAsync("C:\\repo", default);
        Assert.Equal("openai", providers.All[0].Id);
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.DoesNotContain("secret", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("directory=", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuthAndApiKey_MapToPinnedServerEndpointsWithoutLeakingSecret()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.Contains("authorize", StringComparison.Ordinal)
            ? Json("""{"url":"https://login.example/","method":"code","instructions":"Login"}""") : Json("true"));
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "user", "server-password", handler);
        var auth = await api.BeginOAuthAsync("openai", 1, new Dictionary<string, string> { ["tenant"] = "x" }, null, default);
        Assert.Equal("https://login.example/", auth.Url);
        Assert.True(await api.CompleteOAuthAsync("openai", 1, "callback-code", null, default));
        Assert.True(await api.SetApiKeyAsync("openai", "api-secret", null, default));
        Assert.True(await api.RemoveAuthAsync("openai", default));
        Assert.Equal(new[] { "/provider/openai/oauth/authorize", "/provider/openai/oauth/callback", "/auth/openai", "/auth/openai" }, handler.Requests.Select(x => x.RequestUri!.AbsolutePath));
        Assert.All(handler.Requests, request => Assert.DoesNotContain("api-secret", request.RequestUri!.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionPromptAbortDiffAndPermission_MapCorrectly()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session" => Json("""[{"id":"ses_1","title":"t","time":{"created":1,"updated":2}}]"""),
            "/session/ses_1" when request.Method == HttpMethod.Post => Json("""{"id":"ses_1","title":"t","time":{"created":1,"updated":2}}"""),
            "/session/ses_1/prompt_async" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/session/ses_1/diff" => Json("""[{"file":"a.cs","patch":"--- a.cs\n+++ a.cs","additions":1,"deletions":0,"status":"modified"}]"""),
            _ => Json("true")
        });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        Assert.Single(await api.ListSessionsAsync(null, 10, null, default));
        await api.SendPromptAsync("ses_1", "msg_1", new object[] { new { type = "text", text = "hi" } }, "openai", "gpt", null, null, default);
        Assert.True(await api.AbortAsync("ses_1", null, default));
        Assert.Single(await api.GetDiffAsync("ses_1", null, null, default));
        Assert.True(await api.ReplyPermissionAsync("perm_1", "once", null, null, default));
        Assert.Contains(handler.Requests, x => x.RequestUri!.AbsolutePath == "/session/ses_1/prompt_async");
        Assert.Contains(handler.Requests, x => x.RequestUri!.AbsolutePath == "/session/ses_1/abort");
        Assert.Contains(handler.Requests, x => x.RequestUri!.AbsolutePath == "/permission/perm_1/reply");
    }

    [Fact]
    public async Task SessionLifecycleMapsCreateReadUpdateForkChildrenAndDelete()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session" => Json("""{"id":"ses_1","title":"new","directory":"C:\\repo","time":{"created":1,"updated":2}}"""),
            "/session/ses_1/fork" => Json("""{"id":"ses_2","parentID":"ses_1","title":"fork","time":{"created":2,"updated":3}}"""),
            "/session/ses_1/children" => Json("""[{"id":"ses_2","parentID":"ses_1","title":"fork","time":{"created":2,"updated":3}}]"""),
            "/session/ses_1" when request.Method == HttpMethod.Delete => Json("true"),
            "/session/ses_1" => Json("""{"id":"ses_1","title":"updated","time":{"created":1,"updated":3,"archived":3}}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        Assert.Equal("ses_1", (await api.CreateSessionAsync("C:\\repo", "new", "build", ("openai", "gpt"), default)).Id);
        Assert.Equal("ses_1", (await api.GetSessionAsync("ses_1", "C:\\repo", default)).Id);
        Assert.Equal("updated", (await api.UpdateSessionAsync("ses_1", "updated", 3, "C:\\repo", default)).Title);
        Assert.Equal("ses_2", (await api.ForkSessionAsync("ses_1", "msg_1", "C:\\repo", default)).Id);
        Assert.Single(await api.GetChildrenAsync("ses_1", "C:\\repo", default));
        Assert.True(await api.DeleteSessionAsync("ses_1", "C:\\repo", default));
    }

    [Fact]
    public async Task NonSuccessErrorIsMappedAndSecretsAreRedacted()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("api_key=top-secret Authorization: Bearer abcdefghijklmnop")
        });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        var error = await Assert.ThrowsAsync<OpenCodeApiException>(() => api.GetHealthAsync(default));
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain("top-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedPayloadIsRejectedBeforeDeserialization()
    {
        var handler = new RecordingHandler(_ => Json(new string('x', 2048)));
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler, 1024);
        await Assert.ThrowsAsync<OpenCodePayloadTooLargeException>(() => api.GetHealthAsync(default));
    }

    [Fact]
    public async Task CancellationPropagatesThroughHttp()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("true");
        });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetHealthAsync(cancellation.Token));
    }

    [Fact]
    public void DescriptorIsTruthfulAboutPermissionsAndUnsupportedSteering()
    {
        var adapter = new OpenCodeAdapter();
        Assert.Equal(SecurityEnforcementKind.PermissionGate, adapter.Descriptor.Security.EnforcementKind);
        Assert.False(adapter.Descriptor.Security.NetworkRestricted);
        Assert.False(adapter.Descriptor.Security.ProcessRestricted);
        Assert.False(adapter.Descriptor.Capabilities.CanSteerActiveTurn);
        Assert.False(adapter.Descriptor.Capabilities.CanReplayEvents);
    }

    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
    public List<HttpRequestMessage> Requests { get; } = new();

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : this((request, _) => Task.FromResult(response(request))) { }

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(Clone(request));
        return _response(request, cancellationToken);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
