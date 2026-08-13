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

internal sealed class FakeNativeRuntime : ITlahNativeRuntime
{
    private readonly ConcurrentDictionary<Guid, Chat> _chats = new();
    private readonly ConcurrentDictionary<Guid, List<Message>> _messages = new();
    public bool Configured { get; set; }
    public bool Disposed { get; private set; }
    public string? CapturedSecret { get; private set; }
    public Exception? ConfigureException { get; set; }
    public TaskCompletionSource<bool>? RunBlock { get; set; }
    public bool ApprovalSet { get; private set; }
    public bool Cancelled { get; private set; }
    public bool EmitApproval { get; set; }
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "gpt-4o";
    public Guid RunId { get; } = Guid.NewGuid();
    public Guid InvocationId { get; } = Guid.NewGuid();

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
        Task.FromResult<IReadOnlyList<ChatSummaryDto>>(_chats.Values
            .Where(chat => includeArchived || !chat.IsArchived)
            .OrderBy(chat => chat.CreatedAt)
            .Select(chat => new ChatSummaryDto(chat.Id, chat.Title, chat.UpdatedAt, _messages.GetValueOrDefault(chat.Id)?.Count ?? 0, IsArchived: chat.IsArchived))
            .ToArray());

    public Task<Chat> GetChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult(_chats.TryGetValue(chatId, out var chat) ? chat : throw new InvalidOperationException("Chat not found."));

    public Task<Chat> CreateChatAsync(string title, string workspaceRoot, CancellationToken cancellationToken)
    {
        var chat = new Chat { Title = title };
        _chats[chat.Id] = chat;
        _messages[chat.Id] = [];
        return Task.FromResult(chat);
    }

    public Task<Chat> SetArchivedAsync(Guid chatId, bool archived, CancellationToken cancellationToken)
    {
        var chat = _chats[chatId];
        chat.IsArchived = archived;
        chat.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(chat);
    }

    public Task<Chat> RenameAsync(Guid chatId, string title, CancellationToken cancellationToken)
    {
        var chat = _chats[chatId];
        chat.Title = title;
        return Task.FromResult(chat);
    }

    public Task<IReadOnlyList<Message>> ReadMessagesAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Message>>(_messages[chatId]);

    public Task SetModelAsync(Guid chatId, string model, CancellationToken cancellationToken)
    {
        Model = model;
        return Task.CompletedTask;
    }

    public async Task<SendMessageResult> RunAsync(Guid chatId, string prompt, AgentRunOptions options, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var user = new Message { ChatId = chatId, Role = "user", Content = prompt, SequenceNum = 1, CreatedAt = now };
        var assistant = new Message { ChatId = chatId, Role = "assistant", Content = "native answer", SequenceNum = 2, CreatedAt = now };
        _messages[chatId].Add(user);
        options.Progress?.Report(new AgentProgressUpdate(RunId, 1, AgentEventTypes.RunStarted, "info", "Native run started.", now,
            Snapshot(chatId, AgentRunStatuses.Running)));
        options.OutputStream?.Report(new LlmStreamUpdate("native ", "native "));
        if (EmitApproval)
        {
            options.Progress?.Report(new AgentProgressUpdate(RunId, 2, AgentEventTypes.ApprovalRequested, "warning", "Tool requires approval.", now,
                Snapshot(chatId, AgentRunStatuses.AwaitingApproval), ToolInvocationId: InvocationId,
                DataJson: "{\"toolName\":\"file_write\",\"arguments\":{\"path\":\"a.txt\"},\"safetyLevel\":\"write\"}"));
            return Result(chatId, user, assistant, AgentRunStatuses.AwaitingApproval);
        }
        if (RunBlock is not null)
            await RunBlock.Task.WaitAsync(cancellationToken);
        options.OutputStream?.Report(new LlmStreamUpdate("answer", "native answer", IsFinal: true));
        _messages[chatId].Add(assistant);
        return Result(chatId, user, assistant, AgentRunStatuses.Completed);
    }

    public Task<SendMessageResult> ResumeRunAsync(Guid runId, AgentRunOptions options, CancellationToken cancellationToken)
    {
        var chatId = _chats.Keys.Single();
        var now = DateTime.UtcNow;
        var user = _messages[chatId].Single(message => message.Role == "user");
        var assistant = new Message { ChatId = chatId, Role = "assistant", Content = "approved", SequenceNum = 2, CreatedAt = now };
        _messages[chatId].Add(assistant);
        options.Progress?.Report(new AgentProgressUpdate(RunId, 3, AgentEventTypes.ApprovalGranted, "info", "Approved.", now,
            Snapshot(chatId, AgentRunStatuses.Running), ToolInvocationId: InvocationId));
        return Task.FromResult(Result(chatId, user, assistant, AgentRunStatuses.Completed));
    }

    public Task<AgentRunSnapshot?> GetLatestRunAsync(Guid chatId, CancellationToken cancellationToken) =>
        Task.FromResult<AgentRunSnapshot?>(Snapshot(chatId, AgentRunStatuses.Running));

    public Task SetApprovalAsync(Guid invocationId, bool approved, string scope, string? amendedArguments, CancellationToken cancellationToken)
    {
        ApprovalSet = approved;
        return Task.CompletedTask;
    }

    public Task CancelRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        Cancelled = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private AgentRunSnapshot Snapshot(Guid chatId, string status) =>
        new(RunId, chatId, Guid.NewGuid(), status, 1, 48, null, 0,
            status == AgentRunStatuses.AwaitingApproval ? new ToolInvocationSnapshot(InvocationId, "file_write", "{}", ToolInvocationStatuses.AwaitingApproval) : null);

    private SendMessageResult Result(Guid chatId, Message user, Message assistant, string status)
    {
        var turn = new Turn { ChatId = chatId };
        user.TurnId = turn.Id;
        assistant.TurnId = turn.Id;
        return new SendMessageResult(turn, user, assistant,
            new RawRequest { TurnId = turn.Id },
            new RawResponse { TurnId = turn.Id, TokenUsageJson = "{\"input_tokens\":12,\"output_tokens\":4}" },
            Snapshot(chatId, status));
    }
}

internal static class AdapterTestFactory
{
    public static async Task<(TlahKernelAdapter Adapter, SessionRef Session)> CreateAsync(FakeNativeRuntime runtime, string root)
    {
        var adapter = new TlahKernelAdapter(runtime);
        var profile = new KernelProfile("test-profile", TlahKernelAdapter.Id, "Test", root, new Dictionary<string, string>(), true);
        await adapter.InitializeAsync(new KernelInitializationContext("1.0.0", profile, root, root, false));
        string workspace = System.IO.Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var created = await adapter.CreateSessionAsync(new CreateSessionRequest(
            new WorkspaceDescriptor("workspace", workspace, [], true), "Test chat", null, null));
        return (adapter, created.Session);
    }
}
