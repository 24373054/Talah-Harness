namespace Talah.Harness.Contracts;

/// <summary>
/// Defines the transport boundary for provider credentials. Remote API keys may
/// only be sent to HTTPS endpoints; plaintext HTTP is limited to the local host
/// for development providers that never leave the machine.
/// </summary>
public static class ApiKeyEndpointPolicy
{
    public static void Validate(Uri? baseUri, string parameterName)
    {
        if (baseUri is null)
        {
            return;
        }

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Provider base URI must be absolute.", parameterName);
        }

        if (!string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException("Provider base URI must not contain embedded credentials.", parameterName);
        }

        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && baseUri.IsLoopback)
        {
            return;
        }

        throw new ArgumentException(
            "Provider base URI must use HTTPS; HTTP is permitted only for a loopback development endpoint.",
            parameterName);
    }
}
