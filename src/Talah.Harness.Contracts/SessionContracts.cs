namespace Talah.Harness.Contracts;

public static class SessionSecurityMetadata
{
    public const string ApprovalMode = "host.security.approvalMode";
    public const string SandboxMode = "host.security.sandboxMode";
}

public sealed record WorkspaceDescriptor(
    string WorkspaceId,
    string RootPath,
    IReadOnlyList<string> AdditionalRoots,
    bool IsTrusted);

public sealed record SessionRef(
    string AdapterId,
    string ProfileId,
    string NativeSessionId,
    string? ParentNativeSessionId = null,
    string? WorkspaceId = null);

public enum SessionStatus
{
    Idle,
    Running,
    WaitingForApproval,
    Paused,
    Completed,
    Failed,
    Archived
}

public sealed record KernelSessionSummary(
    SessionRef Session,
    string Title,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Preview,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record CreateSessionRequest(
    WorkspaceDescriptor Workspace,
    string? Title,
    string? ModelId,
    string? AgentId,
    IReadOnlyDictionary<string, string>? Options = null);

public enum ForkPointKind
{
    Turn,
    Message,
    Item
}

public sealed record NativeForkPoint(
    ForkPointKind Kind,
    string NativeId);

public sealed record ForkSessionRequest(
    SessionRef Session,
    NativeForkPoint? Point = null,
    string? Title = null);

public sealed record TurnInput(
    IReadOnlyList<ContentBlock> Content,
    IReadOnlyList<string>? ReferencedPaths = null);

public sealed record TurnOptions(
    string? ModelId,
    string? ApprovalMode,
    string? SandboxMode,
    string? AgentId,
    IReadOnlyDictionary<string, string>? VendorOptions = null);

public sealed record KernelTurn(
    SessionRef Session,
    string NativeTurnId,
    TurnStatus Status,
    DateTimeOffset StartedAt);

public enum TurnStatus
{
    Running,
    WaitingForApproval,
    Completed,
    Cancelled,
    Failed
}

public sealed record KernelModel(
    string ModelId,
    string DisplayName,
    string? Description,
    bool IsDefault,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record KernelDiff(
    SessionRef Session,
    string UnifiedDiff,
    IReadOnlyList<string> ChangedPaths,
    bool IsTruncated);

