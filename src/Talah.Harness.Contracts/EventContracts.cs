using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talah.Harness.Contracts;

public enum KernelEventKind
{
    AdapterStatusChanged,
    AuthenticationChanged,
    SessionCreated,
    SessionMetadataChanged,
    SessionClosed,
    TurnStarted,
    TurnCompleted,
    TurnFailed,
    TurnCancelled,
    ContentDelta,
    ItemStarted,
    ItemUpdated,
    ItemCompleted,
    PermissionRequested,
    ElicitationRequested,
    UsageUpdated,
    Diagnostic,
    CapabilityChanged
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ContentDeltaEventData), "content-delta")]
[JsonDerivedType(typeof(ItemEventData), "item")]
[JsonDerivedType(typeof(PermissionEventData), "permission")]
[JsonDerivedType(typeof(ElicitationEventData), "elicitation")]
[JsonDerivedType(typeof(UsageEventData), "usage")]
[JsonDerivedType(typeof(DiagnosticEventData), "diagnostic")]
[JsonDerivedType(typeof(StatusEventData), "status")]
[JsonDerivedType(typeof(SessionEventData), "session")]
[JsonDerivedType(typeof(TurnEventData), "turn")]
public abstract record KernelEventData;

public sealed record ContentDeltaEventData(
    string Channel,
    string Delta,
    string? Format = null) : KernelEventData;

public sealed record ItemEventData(KernelItem Item) : KernelEventData;

public sealed record PermissionEventData(PermissionRequest Request) : KernelEventData;

public sealed record ElicitationEventData(ElicitationRequest Request) : KernelEventData;

public sealed record UsageEventData(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    decimal? EstimatedCost,
    string? Currency) : KernelEventData;

public sealed record DiagnosticEventData(KernelDiagnostic Diagnostic) : KernelEventData;

public sealed record StatusEventData(
    KernelAvailability Availability,
    string Summary) : KernelEventData;

public sealed record SessionEventData(KernelSessionSummary Session) : KernelEventData;

public sealed record TurnEventData(
    TurnStatus Status,
    string? Summary = null) : KernelEventData;

public sealed record KernelEvent(
    string AdapterId,
    string ProfileId,
    string? NativeSessionId,
    string? NativeTurnId,
    string? NativeItemId,
    long Sequence,
    DateTimeOffset Timestamp,
    KernelEventKind Kind,
    KernelEventData Data,
    JsonElement? VendorData = null,
    string? NativeEventId = null);

