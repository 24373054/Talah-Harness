using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Codex.Tests;

public sealed class DeepSeekLiveTests
{
    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task ExternalDeepSeekKeyCreatesSessionStreamsContentAndCancelsCleanly()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("TALAH_DEEPSEEK_LIVE_TEST"), "1", StringComparison.Ordinal),
            "Set TALAH_DEEPSEEK_LIVE_TEST=1 and DEEPSEEK_API_KEY to run the billable DeepSeek connectivity test.");
        string? apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "DEEPSEEK_API_KEY is not set.");
        string? codexPath = Environment.GetEnvironmentVariable("TALAH_CODEX_PATH");
        if (string.IsNullOrWhiteSpace(codexPath))
        {
            string? onPath = FindCodexOnPath();
            codexPath = onPath;
        }
        Skip.If(string.IsNullOrWhiteSpace(codexPath) || !File.Exists(codexPath), "TALAH_CODEX_PATH does not point to codex.exe and no PATH-native codex.exe was found.");

        int[] codexBefore = Process.GetProcessesByName("codex").Select(process => process.Id).ToArray();
        string root = Path.Combine(Path.GetTempPath(), "TalahHarnessCodexDeepSeekLive", Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        KernelProfile profile = new(
            "live",
            CodexKernelAdapter.CodexAdapterId,
            "Codex DeepSeek live",
            Path.Combine(root, "profiles", "codex", "live"),
            new Dictionary<string, string> { ["TALAH_CODEX_PATH"] = codexPath },
            true);
        var context = new KernelInitializationContext("1.0.0-live", profile, Path.Combine(root, "logs"), Path.Combine(root, "schemas"), false);

        Exception? testFailure = null;
        bool leakedProcess = false;
        try
        {
            var factory = new CodexAdapterFactory();
            IKernelAdapter configured = await factory.CreateAsync(profile);
            await configured.InitializeAsync(context);
            await configured.ConfigureApiKeyAsync(new ApiKeyCredential("deepseek", apiKey!));
            await configured.DisposeAsync();

            IKernelAdapter adapter = await factory.CreateAsync(profile);
            await using var _ = adapter;
            await adapter.InitializeAsync(context);
            AuthenticationState auth = await adapter.GetAuthenticationStateAsync();
            Assert.Equal(AuthenticationStatus.SignedIn, auth.Status);

            IReadOnlyList<KernelModel> models = await adapter.ListModelsAsync();
            Assert.Contains(models, model => model.ModelId == "deepseek-v4-flash");
            Assert.Contains(models, model => model.ModelId == "deepseek-v4-pro");

            KernelSessionSummary session = await adapter.CreateSessionAsync(
                new CreateSessionRequest(
                    new WorkspaceDescriptor("w-live", workspace, Array.Empty<string>(), true),
                    "DeepSeek live",
                    "deepseek-v4-flash",
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
                new TurnOptions("deepseek-v4-flash", null, null, null));
            try
            {
                await modelContentSeen.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch (TimeoutException)
            {
            }
            watchLifetime.Cancel();
            try { await watchTask; } catch (OperationCanceledException) { }

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
            int[] codexAfter = Process.GetProcessesByName("codex").Select(process => process.Id).ToArray();
            leakedProcess = codexAfter.Except(codexBefore).Any();
        }

        if (testFailure is not null)
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        Assert.False(leakedProcess, "A kernel process leaked after the live test.");
    }

    private static string? FindCodexOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string root = Path.Combine(segment, "node_modules", "@openai", "codex", "node_modules");
                if (!Directory.Exists(root)) continue;
                string? executable = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(candidate => candidate.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
                if (executable is not null) return executable;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
        return null;
    }
}
