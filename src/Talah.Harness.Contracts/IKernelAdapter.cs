namespace Talah.Harness.Contracts;

public interface IKernelAdapter : IAsyncDisposable
{
    string AdapterId { get; }

    KernelDescriptor Descriptor { get; }

    ValueTask InitializeAsync(
        KernelInitializationContext context,
        CancellationToken cancellationToken = default);

    Task<KernelHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<AuthenticationState> GetAuthenticationStateAsync(
        CancellationToken cancellationToken = default);

    Task<LoginChallenge> BeginLoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default);

    Task CancelLoginAsync(string loginId, CancellationToken cancellationToken = default);

    Task ConfigureApiKeyAsync(
        ApiKeyCredential credential,
        CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KernelModel>> ListModelsAsync(
        CancellationToken cancellationToken = default);

    Task<ResultPage<KernelSessionSummary>> ListSessionsAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    Task<KernelSessionSummary> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<KernelSessionSummary> ResumeSessionAsync(
        SessionRef session,
        CancellationToken cancellationToken = default);

    Task<KernelSessionSummary> ForkSessionAsync(
        ForkSessionRequest request,
        CancellationToken cancellationToken = default);

    Task ArchiveSessionAsync(
        SessionRef session,
        CancellationToken cancellationToken = default);

    Task<KernelTurn> StartTurnAsync(
        SessionRef session,
        TurnInput input,
        TurnOptions options,
        CancellationToken cancellationToken = default);

    Task SteerTurnAsync(
        SessionRef session,
        string nativeTurnId,
        TurnInput input,
        CancellationToken cancellationToken = default);

    Task CancelTurnAsync(
        SessionRef session,
        string nativeTurnId,
        CancellationToken cancellationToken = default);

    Task RespondToPermissionAsync(
        PermissionResponse response,
        CancellationToken cancellationToken = default);

    Task RespondToElicitationAsync(
        ElicitationResponse response,
        CancellationToken cancellationToken = default);

    Task<ResultPage<KernelItem>> ReadHistoryAsync(
        SessionRef session,
        PageRequest request,
        CancellationToken cancellationToken = default);

    Task<KernelDiff?> ReadDiffAsync(
        SessionRef session,
        string? nativeTurnOrItemId = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<KernelEvent> WatchEventsAsync(
        CancellationToken cancellationToken = default);
}

public interface IKernelAdapterFactory
{
    string AdapterId { get; }

    ValueTask<IKernelAdapter> CreateAsync(
        KernelProfile profile,
        CancellationToken cancellationToken = default);
}

public interface IInteractiveLoginCompletionAdapter
{
    Task CompleteLoginAsync(
        string loginId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default);
}

public interface ISessionRenameAdapter
{
    Task<KernelSessionSummary> RenameSessionAsync(
        SessionRef session,
        string title,
        CancellationToken cancellationToken = default);
}
