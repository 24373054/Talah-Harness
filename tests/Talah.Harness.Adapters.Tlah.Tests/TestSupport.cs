using System.Collections.Concurrent;
using Talah.Harness.Contracts;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace Talah.Harness.Adapters.Tlah.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tlah-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeNativeRuntimeState
{
    public ConcurrentDictionary<Guid, Chat> Chats { get; } = new();
    public ConcurrentDictionary<Guid, List<Message>> Messages { get; } = new();
    public Guid RunId { get; } = Guid.NewGuid();
    public Guid InvocationId { get; } = Guid.NewGuid();
    public Guid NativeTurnId { get; } = Guid.NewGuid();
    public AgentRunSnapshot? LatestRun { get; set; }
    public bool? LastApprovalApproved { get; set; }
    public int RunCallCount;
    public int ResumeCallCount;
    public int ApprovalDecisionCount;
    public int DestructiveExecutionCount;
    public bool Cancelled;
}

internal sealed class FakeNativeRuntime : ITlahNativeRuntime
{
    private readonly FakeNativeRuntimeState _state;
    private ConcurrentDictionary<Guid, Chat> Chats => _state.Chats;
    private ConcurrentDictionary<Guid, List<Message>> Messages => _state.Messages;

    public FakeNativeRuntime() : this(new FakeNativeRuntimeState())
    {
    }

    public FakeNativeRuntime(FakeNativeRuntimeState state)
    {
        _state = state;
    }

    public bool Configured { get; set; }
    public bool Disposed { get; private set; }
    public string? CapturedSecret { get; private set; }
    public Exception? ConfigureException { get; set; }
    public TaskCompletionSource<bool>? RunBlock { get; set; }
    public bool ApprovalSet => _state.LastApprovalApproved == true;
    public bool Cancelled => _state.Cancelled;
    public bool RunCancellationObserved { get; private set; }
    public bool IgnoreRunCancellation { get; set; }
    public bool EmitApproval { get; set; }
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "gpt-4o";
    public Guid RunId => _state.RunId;
    public Guid InvocationId => _state.InvocationId;

    public Task InitializeAsync(string dataRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        return Task.CompletedTask;
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(Configured);
    public Task<GlobalSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(
        new GlobalSettingsDto(Provider, Configured ? "sk-...MASKED" : string.Empty, "https://api.openai.com", Model, false, "auto", 0.7, 4096, "", "user"));

    public Task ConfigureAsync(string provider, string secret, Uri? baseUri, CancellationToken cancellationToken)
    {
        CapturedSecret = secret;
        if (ConfigureException is not null)
            throw ConfigureException;
        Provider = provider;
        Configured = true;
        return Task.CompletedTask;
    }

    public Task ClearCredentialAsync(CancellationToken cancellationToken)
    {
        Configured = false;
        CapturedSecret = null;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProviderInfo>>(ProviderInfo.Supported);

    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["gpt-4o", "gpt-4.1"]);

    public Task<IReadOnlyList<ChatSummaryDto>> ListChatsAsync(bool includeArchived, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChatSummaryDto>>(Chats.Values
            .Where(chat => includeArchived || !chat.IsArchived)
            .OrderBy(chat => chat.CreatedAt)
            .Select(chat => new ChatSummaryDto(chat.Id, chat.Title, chat.UpdatedAt, Messages.GetValueOrDefault(chat.Id)?.Count ?? 0, IsArchived: chat.IsArchived))
            .ToArray());

    public Task<Chat> GetChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult(Chats.TryGetValue(chatId, out var chat) ? chat : throw new InvalidOperationException("Chat not found."));

    public Task<Chat> CreateChatAsync(string title, string workspaceRoot, CancellationToken cancellationToken)
    {
        var chat = new Chat { Title = title };
        Chats[chat.Id] = chat;
        Messages[chat.Id] = [];
        return Task.FromResult(chat);
    }

    public Task<Chat> SetArchivedAsync(Guid chatId, bool archived, CancellationToken cancellationToken)
    {
        var chat = Chats[chatId];
        chat.IsArchived = archived;
        chat.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(chat);
    }

    public Task<Chat> RenameAsync(Guid chatId, string title, CancellationToken cancellationToken)
    {
        var chat = Chats[chatId];
        chat.Title = title;
        return Task.FromResult(chat);
    }

    public Task<IReadOnlyList<Message>> ReadMessagesAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Message>>(Messages[chatId]);

    public Task SetModelAsync(Guid chatId, string model, CancellationToken cancellationToken)
    {
        Model = model;
        return Task.CompletedTask;
    }

    public async Task<SendMessageResult> RunAsync(Guid chatId, string prompt, AgentRunOptions options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _state.RunCallCount);
        var now = DateTime.UtcNow;
        var user = new Message { ChatId = chatId, Role = "user", Content = prompt, SequenceNum = 1, CreatedAt = now };
        var assistant = new Message { ChatId = chatId, Role = "assistant", Content = "native answer", SequenceNum = 2, CreatedAt = now };
        Messages[chatId].Add(user);
        _state.LatestRun = Snapshot(chatId, AgentRunStatuses.Running);
        options.Progress?.Report(new AgentProgressUpdate(RunId, 1, AgentEventTypes.RunStarted, "info", "Native run started.", now,
            _state.LatestRun));
        options.OutputStream?.Report(new LlmStreamUpdate("native ", "native "));
        if (EmitApproval)
        {
            _state.LatestRun = Snapshot(chatId, AgentRunStatuses.AwaitingApproval);
            options.Progress?.Report(new AgentProgressUpdate(RunId, 2, AgentEventTypes.ApprovalRequested, "warning", "Tool requires approval.", now,
                _state.LatestRun, ToolInvocationId: InvocationId,
                DataJson: "{\"toolName\":\"file_write\",\"arguments\":{\"path\":\"a.txt\"},\"safetyLevel\":\"write\"}"));
            return Result(chatId, user, assistant, AgentRunStatuses.AwaitingApproval);
        }
        if (RunBlock is not null)
        {
            try
            {
                if (IgnoreRunCancellation)
                    await RunBlock.Task;
                else
                    await RunBlock.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RunCancellationObserved = true;
                throw;
            }
        }
        options.OutputStream?.Report(new LlmStreamUpdate("answer", "native answer", IsFinal: true));
        Messages[chatId].Add(assistant);
        return Result(chatId, user, assistant, AgentRunStatuses.Completed);
    }

    public Task<SendMessageResult> ResumeRunAsync(Guid runId, AgentRunOptions options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _state.ResumeCallCount);
        if (runId != RunId)
            throw new InvalidOperationException("Unexpected native run id.");
        if (_state.LastApprovalApproved == true)
            Interlocked.Increment(ref _state.DestructiveExecutionCount);
        var chatId = Chats.Keys.Single();
        var now = DateTime.UtcNow;
        var user = Messages[chatId].Single(message => message.Role == "user");
        var assistant = new Message { ChatId = chatId, Role = "assistant", Content = "approved", SequenceNum = 2, CreatedAt = now };
        Messages[chatId].Add(assistant);
        _state.LatestRun = Snapshot(chatId, AgentRunStatuses.Running);
        options.Progress?.Report(new AgentProgressUpdate(RunId, 3, AgentEventTypes.ApprovalGranted, "info", "Approved.", now,
            _state.LatestRun, ToolInvocationId: InvocationId));
        return Task.FromResult(Result(chatId, user, assistant, AgentRunStatuses.Completed));
    }

    public Task<AgentRunSnapshot?> GetLatestRunAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult(_state.LatestRun is { ChatId: var latestChatId } && latestChatId == chatId
            ? _state.LatestRun
            : null);

    public Task SetApprovalAsync(Guid invocationId, bool approved, string scope, string? amendedArguments, CancellationToken cancellationToken)
    {
        if (invocationId != InvocationId)
            throw new InvalidOperationException("Unexpected native invocation id.");
        Interlocked.Increment(ref _state.ApprovalDecisionCount);
        _state.LastApprovalApproved = approved;
        Guid chatId = Chats.Keys.Single();
        _state.LatestRun = Snapshot(chatId, AgentRunStatuses.Paused);
        return Task.CompletedTask;
    }

    public Task CancelRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        _state.Cancelled = true;
        if (Chats.Count == 1)
            _state.LatestRun = Snapshot(Chats.Keys.Single(), AgentRunStatuses.Cancelled);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private AgentRunSnapshot Snapshot(Guid chatId, string status) =>
        new(RunId, chatId, _state.NativeTurnId, status, 1, 48, null, 0,
            status == AgentRunStatuses.AwaitingApproval
                ? new ToolInvocationSnapshot(
                    InvocationId,
                    "file_write",
                    "{\"path\":\"a.txt\"}",
                    ToolInvocationStatuses.AwaitingApproval,
                    "write",
                    "Writes the requested file.",
                    "{\"level\":\"write\"}")
                : null);

    private SendMessageResult Result(Guid chatId, Message user, Message assistant, string status)
    {
        var turn = new Turn { ChatId = chatId };
        user.TurnId = turn.Id;
        assistant.TurnId = turn.Id;
        _state.LatestRun = Snapshot(chatId, status);
        return new SendMessageResult(turn, user, assistant,
            new RawRequest { TurnId = turn.Id },
            new RawResponse { TurnId = turn.Id, TokenUsageJson = "{\"input_tokens\":12,\"output_tokens\":4}" },
            _state.LatestRun);
    }
}

internal static class AdapterTestFactory
{
    public static async Task<(TlahKernelAdapter Adapter, SessionRef Session)> CreateAsync(FakeNativeRuntime runtime, string root)
    {
        TlahKernelAdapter adapter = await InitializeAsync(runtime, root);
        string workspace = System.IO.Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var created = await adapter.CreateSessionAsync(new CreateSessionRequest(
            new WorkspaceDescriptor("workspace", workspace, [], true), "Test chat", null, null));
        return (adapter, created.Session);
    }

    public static async Task<TlahKernelAdapter> InitializeAsync(FakeNativeRuntime runtime, string root)
    {
        var adapter = new TlahKernelAdapter(runtime);
        var profile = new KernelProfile("test-profile", TlahKernelAdapter.Id, "Test", root, new Dictionary<string, string>(), true);
        await adapter.InitializeAsync(new KernelInitializationContext("1.0.0", profile, root, root, false));
        return adapter;
    }
}
