namespace Talah.Harness.Contracts;

public enum AuthenticationStatus
{
    Unknown,
    SignedOut,
    SigningIn,
    SignedIn,
    Expired,
    Failed
}

public enum AuthenticationMethod
{
    Browser,
    DeviceCode,
    ApiKey,
    ExternalCli,
    VendorDefined
}

public sealed record AuthenticationState(
    AuthenticationStatus Status,
    string? AccountLabel,
    string? PlanLabel,
    IReadOnlyList<AuthenticationMethod> SupportedMethods,
    string? Message = null);

public sealed record LoginRequest(
    AuthenticationMethod Method,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record LoginChallenge(
    string LoginId,
    AuthenticationMethod Method,
    Uri? VerificationUri,
    string? UserCode,
    DateTimeOffset? ExpiresAt,
    string Instructions);

public sealed record ApiKeyCredential(
    string ProviderId,
    string Secret,
    Uri? BaseUri = null,
    IReadOnlyDictionary<string, string>? Options = null);

