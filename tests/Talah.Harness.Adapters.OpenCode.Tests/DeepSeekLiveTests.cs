using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode.Tests;

public sealed class DeepSeekLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ExternalDeepSeekKeyCreatesSessionStreamsEventsAndAbortsCleanly()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TALAH_DEEPSEEK_LIVE_TEST"), "1", StringComparison.Ordinal)) return;
        string? apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        string? executable = Environment.GetEnvironmentVariable("OPENCODE_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

        int[] before = Process.GetProcessesByName("opencode").Select(process => process.Id).ToArray();
        string root = Path.Combine(Path.GetTempPath(), "TalahHarnessOpenCodeDeepSeekLive", Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        KernelProfile profile = new(
            "live",
            OpenCodeAdapter.Id,
            "OpenCode DeepSeek live",
            Path.Combine(root, "profiles", "opencode", "live"),
            new Dictionary<string, string> { ["OPENCODE_EXECUTABLE"] = executable },
            true);
        var context = new KernelInitializationContext("1.0.0-live", profile, Path.Combine(root, "logs"), Path.Combine(root, "schemas"), false);

        Exception? testFailure = null;
        bool leakedProcess = false;
        try
        {
            var configured = new OpenCodeAdapter();
            await configured.InitializeAsync(context);
            await configured.ConfigureApiKeyAsync(new ApiKeyCredential("deepseek", apiKey));
            await configured.DisposeAsync();

            var adapter = new OpenCodeAdapter();
            await using var _ = adapter;
            await adapter.InitializeAsync(context);
            AuthenticationState auth = await adapter.GetAuthenticationStateAsync();
            Assert.Equal(AuthenticationStatus.SignedIn, auth.Status);

            IReadOnlyList<KernelModel> models = await adapter.ListModelsAsync();
            Assert.Contains(models, model => model.ModelId == "deepseek/deepseek-v4-flash");

            KernelSessionSummary session = await adapter.CreateSessionAsync(
                new CreateSessionRequest(
                    new WorkspaceDescriptor("w-live", workspace, Array.Empty<string>(), true),
                    "DeepSeek live",
                    "deepseek/deepseek-v4-flash",
                    null));
            using var watchLifetime = new CancellationTokenSource();
            var received = new StringBuilder();
            var observations = new ConcurrentQueue<string>();
            var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var modelContentSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task watchTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (KernelEvent kernelEvent in adapter.WatchEventsAsync(watchLifetime.Token))
                    {
                        streamStarted.TrySetResult(true);
                        observations.Enqueue(kernelEvent.Kind.ToString());
                        if (kernelEvent.Data is ContentDeltaEventData delta && delta.Channel is "text" or "plan" or "assistant")
                        {
                            lock (received) received.Append(delta.Delta);
                            if (received.ToString().Contains("DEEPSEEK_OK", StringComparison.Ordinal))
                                modelContentSeen.TrySetResult(true);
                        }
                        if (kernelEvent.Data is ItemEventData item && item.Item.Kind != KernelItemKind.UserMessage &&
                            item.Item.Content.OfType<TextContentBlock>().FirstOrDefault() is TextContentBlock completedText)
                        {
                            lock (received) received.Clear().Append(completedText.Text);
                            if (received.ToString().Contains("DEEPSEEK_OK", StringComparison.Ordinal))
                                modelContentSeen.TrySetResult(true);
                        }
                        if (kernelEvent.Kind is KernelEventKind.TurnCompleted or KernelEventKind.TurnFailed or KernelEventKind.TurnCancelled)
                            modelContentSeen.TrySetResult(false);
                    }
                }
                catch (OperationCanceledException) when (watchLifetime.IsCancellationRequested)
                {
                }
            });

            await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            KernelTurn turn = await adapter.StartTurnAsync(
                session.Session,
                new TurnInput([new TextContentBlock("Reply with exactly: DEEPSEEK_OK")]),
                new TurnOptions("deepseek/deepseek-v4-flash", null, null, null));
            try
            {
                await modelContentSeen.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch (TimeoutException)
            {
            }
            watchLifetime.Cancel();
            try { await watchTask; } catch (OperationCanceledException) { }

            if (string.Equals(Environment.GetEnvironmentVariable("TALAH_DEEPSEEK_LIVE_VERBOSE"), "1", StringComparison.Ordinal))
                Console.WriteLine($"DEEPSEEK_LIVE received='{received}' events={string.Join(",", observations)}");
            Assert.True(received.ToString().Contains("DEEPSEEK_OK", StringComparison.Ordinal), "Received: " + (received.ToString()) + " Events: " + string.Join(",", observations));

            await adapter.CancelTurnAsync(session.Session, turn.NativeTurnId);
            await Task.Delay(500);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            for (int attempt = 0; attempt < 5 && Directory.Exists(root); attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    await Task.Delay(500);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }

            await Task.Delay(1500);
            int[] after = Process.GetProcessesByName("opencode").Select(process => process.Id).ToArray();
            leakedProcess = after.Except(before).Any();
        }

        if (testFailure is not null)
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        Assert.False(leakedProcess, "A kernel process leaked after the live test.");
    }
}
