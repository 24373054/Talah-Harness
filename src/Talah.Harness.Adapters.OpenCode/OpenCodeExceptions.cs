namespace Talah.Harness.Adapters.OpenCode;

public class OpenCodeAdapterException : Exception
{
    public OpenCodeAdapterException(string message) : base(message) { }

    public OpenCodeAdapterException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class OpenCodeNotInstalledException : OpenCodeAdapterException
{
    public OpenCodeNotInstalledException(string message) : base(message) { }
}

public sealed class OpenCodeIncompatibleVersionException : OpenCodeAdapterException
{
    public OpenCodeIncompatibleVersionException(string message) : base(message) { }
}

public sealed class OpenCodeApiException : OpenCodeAdapterException
{
    public OpenCodeApiException(int statusCode, string method, string path, string responseBody)
        : base($"OpenCode API {method} {path} failed with HTTP {statusCode}: {Redaction.Redact(responseBody)}")
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        ResponseBody = Redaction.Redact(responseBody);
    }

    public int StatusCode { get; }
    public string Method { get; }
    public string Path { get; }
    public string ResponseBody { get; }
}

public sealed class OpenCodePayloadTooLargeException : OpenCodeAdapterException
{
    public OpenCodePayloadTooLargeException(int maximumBytes)
        : base($"OpenCode event exceeded the configured {maximumBytes} byte limit.") { }
}

internal static class Redaction
{
    private static readonly System.Text.RegularExpressions.Regex AuthorizationPattern =
        new("(?i)(authorization\\s*[:=]\\s*)(?:bearer|basic)\\s+[^\\s,;\\\"]+",
            System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    private static readonly System.Text.RegularExpressions.Regex SecretPattern =
        new("(?i)(authorization|api[_-]?key|token|secret|password)(\\s*[:=]\\s*|\\\"\\s*:\\s*\\\")([^\\s,;\\\"]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return SecretPattern.Replace(AuthorizationPattern.Replace(value, "$1[REDACTED]"), "$1$2[REDACTED]");
    }
}
