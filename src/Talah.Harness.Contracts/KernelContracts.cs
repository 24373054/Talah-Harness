using System.Text.Json;

namespace Talah.Harness.Contracts;

public static class HarnessContract
{
    public const int MajorVersion = 1;
}

public enum KernelAvailability
{
    Unknown,
    NotInstalled,
    Starting,
    Ready,
    Degraded,
    Stopped,
    Failed
}

public enum SecurityEnforcementKind
{
    None,
    PermissionGate,
    OperatingSystemSandbox,
    Container,
    RemoteIsolation
}

public sealed record KernelCapabilities(
    bool CanAuthenticate,
    bool CanUseApiKey,
    bool CanListSessions,
    bool CanResumeSessions,
    bool CanForkSessions,
    bool CanArchiveSessions,
    bool CanSteerActiveTurn,
    bool CanCancelTurn,
    bool CanApproveTools,
    bool CanAmendToolInput,
    bool CanReadHistory,
    bool CanReturnDiffs,
    bool CanConfigureProviders,
    bool CanConfigureMcp,
    bool CanUseSubagents,
    bool CanReplayEvents);

public sealed record SecurityDescriptor(
    SecurityEnforcementKind EnforcementKind,
    string EnforcementOwner,
    IReadOnlyList<string> WritableRoots,
    bool NetworkRestricted,
    bool ProcessRestricted,
    bool IsVerifiedByHost,
    string HumanReadableSummary,
    IReadOnlyList<KernelSecurityPolicyOption>? ApprovalPolicies = null,
    IReadOnlyList<KernelSecurityPolicyOption>? SandboxPolicies = null,
    string? DefaultApprovalPolicy = null,
    string? DefaultSandboxPolicy = null);

public sealed record KernelSecurityPolicyOption(
    string Value,
    string DisplayName,
    string Description,
    bool IsDangerous = false);

public sealed record KernelDescriptor(
    string AdapterId,
    string DisplayName,
    string ProtocolKind,
    string AdapterVersion,
    string? NativeVersion,
    KernelAvailability Availability,
    KernelCapabilities Capabilities,
    SecurityDescriptor Security,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record KernelHealth(
    KernelAvailability Availability,
    string Summary,
    DateTimeOffset CheckedAt,
    IReadOnlyList<KernelDiagnostic> Diagnostics);

public sealed record KernelDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Remediation = null,
    JsonElement? VendorData = null);

public enum DiagnosticSeverity
{
    Trace,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record KernelProfile(
    string ProfileId,
    string AdapterId,
    string DisplayName,
    string DataRoot,
    IReadOnlyDictionary<string, string> Environment,
    bool IsDefault);

public sealed record KernelInitializationContext(
    string HostVersion,
    KernelProfile Profile,
    string LogRoot,
    string SchemaRoot,
    bool DiagnosticMode);

public sealed record PageRequest(int PageSize = 50, string? Cursor = null);

public sealed record ResultPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

