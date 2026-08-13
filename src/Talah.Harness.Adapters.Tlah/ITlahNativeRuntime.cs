using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace Talah.Harness.Adapters.Tlah;

/// <summary>
/// Narrow boundary over the native TLAH Core/Data runtime. The production
/// implementation is <see cref="TlahNativeRuntime"/>; the boundary also keeps
/// contract mapping independently testable without making paid model calls.
/// </summary>
public interface ITlahNativeRuntime : IAsyncDisposable
{
    Task InitializeAsync(string dataRoot, CancellationToken cancellationToken);
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
    Task<GlobalSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);
    Task ConfigureAsync(string provider, string secret, Uri? baseUri, CancellationToken cancellationToken);
    Task ClearCredentialAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSummaryDto>> ListChatsAsync(bool includeArchived, CancellationToken cancellationToken);
    Task<Chat> GetChatAsync(Guid chatId, CancellationToken cancellationToken);
    Task<Chat> CreateChatAsync(string title, string workspaceRoot, CancellationToken cancellationToken);
    Task<Chat> SetArchivedAsync(Guid chatId, bool archived, CancellationToken cancellationToken);
    Task<Chat> RenameAsync(Guid chatId, string title, CancellationToken cancellationToken);
    Task<IReadOnlyList<Message>> ReadMessagesAsync(Guid chatId, CancellationToken cancellationToken);
    Task SetModelAsync(Guid chatId, string model, CancellationToken cancellationToken);
    Task<SendMessageResult> RunAsync(Guid chatId, string prompt, AgentRunOptions options, CancellationToken cancellationToken);
    Task<SendMessageResult> ResumeRunAsync(Guid runId, AgentRunOptions options, CancellationToken cancellationToken);
    Task<AgentRunSnapshot?> GetLatestRunAsync(Guid chatId, CancellationToken cancellationToken);
    Task SetApprovalAsync(Guid invocationId, bool approved, string scope, string? amendedArguments, CancellationToken cancellationToken);
    Task CancelRunAsync(Guid runId, CancellationToken cancellationToken);
}
