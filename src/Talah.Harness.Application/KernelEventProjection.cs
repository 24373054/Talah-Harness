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
        KernelEvent sanitized = SensitiveEventSanitizer.Sanitize(kernelEvent);
        string nativeEventId = CreateIdentity(sanitized);
        EventAppendResult result = await _repository.AppendEventAsync(nativeEventId, sanitized, cancellationToken).ConfigureAwait(false);
        if (result.Inserted)
            await ApplyAsync(sanitized, cancellationToken).ConfigureAwait(false);
        return new StoredCanonicalEvent(result.HostSequence, nativeEventId, sanitized);
    }

    public async Task ApplyAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
    {
        switch (kernelEvent.Data)
        {
            case SessionEventData session:
                await _repository.UpsertProjectedSessionAsync(session.Session, cancellationToken).ConfigureAwait(false);
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
            case ElicitationEventData elicitation:
                await _repository.UpsertElicitationAsync(new StoredElicitation(
                    elicitation.Request.RequestId,
                    kernelEvent.AdapterId,
                    kernelEvent.ProfileId,
                    kernelEvent.NativeSessionId,
                    kernelEvent.NativeTurnId,
                    "pending",
                    JsonSerializer.SerializeToElement(elicitation.Request, JsonOptions),
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
        string identityMaterial = string.IsNullOrWhiteSpace(kernelEvent.NativeEventId)
            ? JsonSerializer.Serialize(kernelEvent, JsonOptions)
            : string.Join('\n', kernelEvent.AdapterId, kernelEvent.ProfileId, kernelEvent.NativeEventId);
        string prefix = string.IsNullOrWhiteSpace(kernelEvent.NativeEventId) ? "event-sha256:" : "native-sha256:";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial))).ToLowerInvariant();
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
