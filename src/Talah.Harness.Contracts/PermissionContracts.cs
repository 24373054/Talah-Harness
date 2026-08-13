using System.Text.Json;

namespace Talah.Harness.Contracts;

public enum PermissionDecisionKind
{
    Deny,
    AllowOnce,
    AllowForSession,
    AllowWithAmendedInput,
    VendorDefined
}

public sealed record ResourceImpact(
    string Kind,
    string Target,
    string Operation,
    string RiskLevel,
    string? Detail = null);

public sealed record PermissionChoice(
    string ChoiceId,
    PermissionDecisionKind Kind,
    string Label,
    string? Description,
    bool AllowsAmendedInput = false);

public sealed record PermissionRequest(
    string PermissionId,
    SessionRef Session,
    string? NativeTurnId,
    string NativeKind,
    string Title,
    string? Reason,
    IReadOnlyList<ResourceImpact> Impacts,
    IReadOnlyList<PermissionChoice> Choices,
    JsonElement? VendorData = null);

public sealed record PermissionResponse(
    string PermissionId,
    string ChoiceId,
    JsonElement? AmendedInput = null);

public sealed record ElicitationRequest(
    string RequestId,
    SessionRef Session,
    string Title,
    string Prompt,
    JsonElement? Schema = null,
    JsonElement? VendorData = null);

public sealed record ElicitationResponse(
    string RequestId,
    bool Cancelled,
    JsonElement? Value = null);

