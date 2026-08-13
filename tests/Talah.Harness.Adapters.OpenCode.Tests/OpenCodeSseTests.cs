using System.Net;
using System.Text;
using System.Text.Json;
using Talah.Harness.Adapters.OpenCode;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode.Tests;

public sealed class OpenCodeSseTests
{
    [Fact]
    public async Task ParserSupportsMultilineDataAndRetainsVendorJson()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("id: evt-1\nevent: update\ndata: {\"type\":\"message.part.delta\",\ndata: \"properties\":{\"delta\":\"hi\"}}\n\n"));
        var values = new List<OpenCodeSseEvent>();
        await foreach (var item in OpenCodeSseClient.ParseAsync(stream, 4096, default)) values.Add(item);
        Assert.Single(values);
        Assert.Equal("evt-1", values[0].Id);
        Assert.Contains('\n', values[0].Data);
        Assert.Equal("message.part.delta", values[0].VendorJson.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReconnectUsesLastEventIdAndDeduplicates()
    {
        var calls = 0;
        var handler = new RecordingHandler(request =>
        {
            calls++;
            var body = calls == 1
                ? "id: 1\ndata: {\"id\":\"vendor-1\",\"type\":\"server.connected\"}\n\n"
                : "id: 1\ndata: {\"id\":\"vendor-1\",\"type\":\"server.connected\"}\n\nid: 2\ndata: {\"id\":\"vendor-2\",\"type\":\"server.connected\"}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        var client = new OpenCodeSseClient(api, bufferCapacity: 1, minimumReconnectDelay: TimeSpan.Zero, maximumReconnectDelay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var values = new List<OpenCodeSseEvent>();
        await foreach (var value in client.WatchAsync(null, cancellation.Token))
        {
            values.Add(value);
            if (values.Count == 2) break;
        }
        Assert.Equal(new[] { "1", "2" }, values.Select(x => x.Id));
        Assert.Equal("1", handler.Requests[1].Headers.GetValues("Last-Event-ID").Single());
    }

    [Fact]
    public async Task BoundedBufferAppliesBackpressureAndPreservesOrder()
    {
        var body = string.Join(string.Empty, Enumerable.Range(0, 20).Select(i => $"id: {i}\ndata: {{\"id\":\"{i}\",\"type\":\"server.connected\"}}\n\n"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        using var api = new OpenCodeApiClient(new Uri("http://127.0.0.1:7777/"), "u", "p", handler);
        var client = new OpenCodeSseClient(api, bufferCapacity: 1, minimumReconnectDelay: TimeSpan.FromHours(1));
        var values = new List<string?>();
        using var cancellation = new CancellationTokenSource();
        await foreach (var value in client.WatchAsync(null, cancellation.Token))
        {
            values.Add(value.Id);
            await Task.Delay(1);
            if (values.Count == 20) { cancellation.Cancel(); break; }
        }
        Assert.Equal(Enumerable.Range(0, 20).Select(x => x.ToString()), values);
    }

    [Fact]
    public async Task ParserRejectsOversizedEvent()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', 2000) + "\n\n"));
        await Assert.ThrowsAsync<OpenCodePayloadTooLargeException>(async () =>
        {
            await foreach (var _ in OpenCodeSseClient.ParseAsync(stream, 1024, default)) { }
        });
    }

    [Fact]
    public void NormalizerMapsDeltaToolPermissionUsageErrorAndUnknown()
    {
        var normalizer = new OpenCodeEventNormalizer("profile");
        Assert.Equal(KernelEventKind.ContentDelta, One("""{"type":"message.part.delta","properties":{"sessionID":"s","messageID":"m","delta":"hi"}}""").Kind);
        Assert.Equal(KernelEventKind.ItemStarted, One("""{"type":"message.part.updated","properties":{"part":{"id":"p","sessionID":"s","messageID":"m","type":"tool","tool":"bash","callID":"c","state":{"status":"running","input":{}}}}}""").Kind);
        Assert.Equal(KernelEventKind.PermissionRequested, One("""{"type":"permission.v2.asked","properties":{"id":"p","sessionID":"s","permission":"bash","patterns":["dir/*"]}}""").Kind);
        Assert.Equal(KernelEventKind.UsageUpdated, One("""{"type":"message.part.updated","properties":{"part":{"id":"p","type":"step-finish","tokens":{"input":1,"output":2},"cost":0.1}}}""").Kind);
        Assert.Equal(KernelEventKind.TurnFailed, One("""{"type":"session.error","properties":{"sessionID":"s","error":{"name":"X"}}}""").Kind);
        Assert.Equal(KernelEventKind.Diagnostic, One("""{"type":"future.event","properties":{"new":true}}""").Kind);

        KernelEvent One(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.Clone();
            return Assert.Single(normalizer.Normalize(new OpenCodeSseEvent(null, null, json, root)));
        }
    }
}
