using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Talah.Harness.Contracts;
using Talah.Harness.Persistence;

namespace Talah.Harness.Application;

internal sealed class KernelEventProjection(CanonicalRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CanonicalRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<StoredCanonicalEvent> PersistAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
    {
        Validate(kernelEvent);
        string nativeEventId = CreateIdentity(kernelEvent);
        EventAppendResult result = await _repository.AppendEventAsync(nativeEventId, kernelEvent, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(kernelEvent, cancellationToken).ConfigureAwait(false);
        return new StoredCanonicalEvent(result.HostSequence, nativeEventId, kernelEvent);
    }

    public async Task ApplyAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
    {
        switch (kernelEvent.Data)
        {
            case SessionEventData session:
                await _repository.UpsertSessionAsync(session.Session, cancellationToken).ConfigureAwait(false);
                break;
            case ItemEventData item when kernelEvent.NativeSessionId is not null:
                {
                    StoredSession? stored = await FindSessionAsync(kernelEvent, cancellationToken).ConfigureAwait(false);
                    if (stored is not null)
                        await _repository.UpsertItemAsync(stored.HostSessionId, kernelEvent.NativeTurnId, item.Item, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case PermissionEventData permission:
                await _repository.UpsertApprovalAsync(new StoredApproval(
                    permission.Request.PermissionId,
                    kernelEvent.AdapterId,
                    kernelEvent.ProfileId,
                    kernelEvent.NativeSessionId,
                    kernelEvent.NativeTurnId,
                    "pending",
                    JsonSerializer.SerializeToElement(permission.Request, JsonOptions),
                    null,
                    kernelEvent.Timestamp,
                    null), cancellationToken).ConfigureAwait(false);
                break;
            case TurnEventData turn when kernelEvent.NativeSessionId is not null && kernelEvent.NativeTurnId is not null:
                {
                    StoredSession? stored = await FindSessionAsync(kernelEvent, cancellationToken).ConfigureAwait(false);
                    if (stored is not null)
                    {
                        await _repository.UpsertTurnAsync(
                            stored.HostSessionId,
                            kernelEvent.NativeTurnId,
                            turn.Status,
                            kernelEvent.Timestamp,
                            IsTerminal(turn.Status) ? kernelEvent.Timestamp : null,
                            turn.Summary,
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }
        }
    }

    private Task<StoredSession?> FindSessionAsync(KernelEvent kernelEvent, CancellationToken cancellationToken) =>
        _repository.GetSessionAsync(
            new SessionRef(kernelEvent.AdapterId, kernelEvent.ProfileId, kernelEvent.NativeSessionId!),
            cancellationToken);

    private static string CreateIdentity(KernelEvent kernelEvent)
    {
        string serialized = JsonSerializer.Serialize(kernelEvent, JsonOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
    }

    private static bool IsTerminal(TurnStatus status) =>
        status is TurnStatus.Completed or TurnStatus.Cancelled or TurnStatus.Failed;

    private static void Validate(KernelEvent kernelEvent)
    {
        ArgumentNullException.ThrowIfNull(kernelEvent);
        if (string.IsNullOrWhiteSpace(kernelEvent.AdapterId) || string.IsNullOrWhiteSpace(kernelEvent.ProfileId))
            throw new InvalidDataException("Kernel events must identify their adapter and profile.");
    }
}
