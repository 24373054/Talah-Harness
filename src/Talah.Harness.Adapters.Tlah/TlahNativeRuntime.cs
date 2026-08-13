using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Workspace;
using TLAHStudio.Data;

namespace Talah.Harness.Adapters.Tlah;

/// <summary>Native, headless composition of the pinned TLAH Core/Data projects.</summary>
public sealed class TlahNativeRuntime : ITlahNativeRuntime
{
    private ServiceProvider? _services;

    public async Task InitializeAsync(string dataRoot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_services is not null, this);
        string root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "tlah.db");
        string sandboxRoot = Path.Combine(root, "sandboxes");

        var services = new ServiceCollection();
        services.AddDbContext<TlahDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<TlahDbContext>());
        services.AddHttpClient("LLM", client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<ISandboxCommandService>(_ => new SandboxCommandService(sandboxRoot));
        services.AddScoped<ILlmService>(sp => new LlmService(
            sp.GetRequiredService<DbContext>(),
            sp.GetRequiredService<IChatService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ISandboxCommandService>()));

        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        TlahDbContext db = scope.ServiceProvider.GetRequiredService<TlahDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        db.Initialize();
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ISettingsService>().IsConfiguredAsync(cancellationToken));

    public Task<GlobalSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ISettingsService>().GetGlobalSettingsMaskedAsync(cancellationToken));

    public Task ConfigureAsync(string provider, string secret, Uri? baseUri, CancellationToken cancellationToken) =>
        InScopeAsync(async sp =>
        {
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            ProviderInfo info = settings.GetSupportedProviders().First(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase));
            await settings.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
                Provider: info.Key,
                ApiKey: secret,
                BaseUrl: baseUri?.AbsoluteUri ?? info.DefaultBaseUrl,
                Model: info.DefaultModel), cancellationToken).ConfigureAwait(false);
        });

    public Task ClearCredentialAsync(CancellationToken cancellationToken) =>
        InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ISettingsService>()
                .UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(ApiKey: string.Empty), cancellationToken)
                .ConfigureAwait(false);
        });

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InScopeAsync<IReadOnlyList<ProviderInfo>>(sp =>
            Task.FromResult(sp.GetRequiredService<ISettingsService>().GetSupportedProviders()));
    }

    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken) =>
        InScopeAsync<IReadOnlyList<string>>(async sp =>
        {
            GlobalSettingsDto settings = await sp.GetRequiredService<ISettingsService>()
                .GetGlobalSettingsMaskedAsync(cancellationToken).ConfigureAwait(false);
            return ProviderModelCatalog.FallbackModels(settings.Provider);
        });

    public Task<IReadOnlyList<ChatSummaryDto>> ListChatsAsync(bool includeArchived, CancellationToken cancellationToken) =>
        InScopeAsync<IReadOnlyList<ChatSummaryDto>>(async sp =>
            await sp.GetRequiredService<IChatService>().ListChatsAsync(includeArchived: includeArchived, ct: cancellationToken).ConfigureAwait(false));

    public Task<Chat> GetChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<IChatService>().GetChatOrThrowAsync(chatId, cancellationToken));

    public Task<Chat> CreateChatAsync(string title, string workspaceRoot, CancellationToken cancellationToken) =>
        InScopeAsync(async sp =>
        {
            Chat chat = await sp.GetRequiredService<IChatService>().CreateChatAsync(title, cancellationToken).ConfigureAwait(false);
            await new WorkspaceRootService().SetRootAsync(chat.Id, workspaceRoot, cancellationToken).ConfigureAwait(false);
            return chat;
        });

    public Task<Chat> SetArchivedAsync(Guid chatId, bool archived, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<IChatService>().SetArchivedAsync(chatId, archived, cancellationToken));

    public Task<Chat> RenameAsync(Guid chatId, string title, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<IChatService>().UpdateChatAsync(chatId, title: title, ct: cancellationToken));

    public Task<IReadOnlyList<Message>> ReadMessagesAsync(Guid chatId, CancellationToken cancellationToken) =>
        InScopeAsync<IReadOnlyList<Message>>(async sp =>
            await sp.GetRequiredService<IChatService>().GetChatMessagesAsync(chatId, cancellationToken).ConfigureAwait(false));

    public Task SetModelAsync(Guid chatId, string model, CancellationToken cancellationToken) =>
        InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ISettingsService>()
                .UpdateChatSettingsAsync(chatId, new ChatSettingsUpdateDto(Model: model), cancellationToken)
                .ConfigureAwait(false);
        });

    public Task<SendMessageResult> RunAsync(Guid chatId, string prompt, AgentRunOptions options, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ILlmService>().RunAgentTaskAsync(chatId, prompt, options: options, ct: cancellationToken));

    public Task<SendMessageResult> ResumeRunAsync(Guid runId, AgentRunOptions options, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ILlmService>().ResumeAgentTaskAsync(runId, options, cancellationToken));

    public Task<AgentRunSnapshot?> GetLatestRunAsync(Guid chatId, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ILlmService>().GetLatestAgentRunAsync(chatId, cancellationToken));

    public Task SetApprovalAsync(Guid invocationId, bool approved, string scope, string? amendedArguments, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ILlmService>().SetAgentToolApprovalAsync(
            invocationId, approved, scope, cancellationToken, amendedArguments));

    public Task CancelRunAsync(Guid runId, CancellationToken cancellationToken) =>
        InScopeAsync(sp => sp.GetRequiredService<ILlmService>().CancelAgentRunAsync(runId, cancellationToken));

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ServiceProvider services = _services ?? throw new InvalidOperationException("The native TLAH runtime is not initialized.");
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider).ConfigureAwait(false);
    }

    private async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        ServiceProvider services = _services ?? throw new InvalidOperationException("The native TLAH runtime is not initialized.");
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await action(scope.ServiceProvider).ConfigureAwait(false);
    }
}
