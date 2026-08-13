using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Talah.Harness.App.Services;

public static class SecureDiagnosticFormatter
{
    private const int MaximumExceptionDepth = 12;
    private const int MaximumMessageLength = 4_096;
    private static readonly Regex AuthorizationHeader = Create(
        "(?i)\\b(authorization|proxy-authorization)(\\s*[:=]\\s*)(bearer|basic|digest)?\\s*([^\\s,;]+)");
    private static readonly Regex CookieHeader = Create(
        "(?i)\\b(cookie|set-cookie)(\\s*[:=]\\s*)([^\\r\\n]+)");
    private static readonly Regex NamedCredential = Create(
        "(?i)\\b(api[_ -]?key|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|secret|password|passwd|credential|client[_ -]?secret)(\\s*[:=]\\s*)([\"']?)([^\"'\\s,;&]+)([\"']?)");
    private static readonly Regex UrlUserInfo = Create(
        "(?i)(https?://)([^/@:\\s]+):([^/@\\s]+)@");
    private static readonly Regex SensitiveQuery = Create(
        "(?i)([?&](?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|credential)=)([^&#\\s]+)");
    private static readonly Regex JwtLike = Create(
        "(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])");

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string redacted = AuthorizationHeader.Replace(value, "$1$2[REDACTED]");
        redacted = CookieHeader.Replace(redacted, "$1$2[REDACTED]");
        redacted = NamedCredential.Replace(redacted, "$1$2[REDACTED]");
        redacted = UrlUserInfo.Replace(redacted, "$1[REDACTED]@");
        redacted = SensitiveQuery.Replace(redacted, "$1[REDACTED]");
        redacted = JwtLike.Replace(redacted, "[REDACTED]");
        return redacted.Length <= MaximumMessageLength ? redacted : redacted[..MaximumMessageLength] + "…[TRUNCATED]";
    }

    public static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var lines = new List<string>();
        var pending = new Queue<(Exception Exception, int Depth)>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((exception, 0));
        while (pending.Count > 0 && lines.Count < MaximumExceptionDepth)
        {
            (Exception current, int depth) = pending.Dequeue();
            if (!visited.Add(current)) continue;
            lines.Add($"{new string('>', Math.Min(depth, 8))}{current.GetType().Name}: {Redact(current.Message)}");
            if (current is AggregateException aggregate)
            {
                foreach (Exception child in aggregate.InnerExceptions) pending.Enqueue((child, depth + 1));
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue((current.InnerException, depth + 1));
            }
        }
        if (pending.Count > 0) lines.Add("[ADDITIONAL INNER EXCEPTIONS OMITTED]");
        return string.Join(" | ", lines);
    }

    private static Regex Create(string pattern) => new(
        pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}

internal sealed class SecureDiagnosticLog
{
    private const long MaximumFileBytes = 512 * 1_024;
    private const int RetainedFiles = 4;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _path;

    public SecureDiagnosticLog(string productRoot)
    {
        _directory = Path.Combine(Path.GetFullPath(productRoot), "logs");
        _path = Path.Combine(_directory, "crash.log");
    }

    public void Write(string source, Exception exception)
    {
        try
        {
            lock (_gate)
            {
                EnsureRestrictedDirectory();
                RotateIfRequired();
                string line = $"{DateTimeOffset.UtcNow:O}\t{SecureDiagnosticFormatter.Redact(source)}\t{SecureDiagnosticFormatter.FormatException(exception)}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception loggingFailure) when (loggingFailure is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // Diagnostic logging cannot replace the original application failure.
        }
    }

    private void EnsureRestrictedDirectory()
    {
        Directory.CreateDirectory(_directory);
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
    }

    private void RotateIfRequired()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaximumFileBytes) return;
        string oldest = $"{_path}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int index = RetainedFiles - 1; index >= 1; index--)
        {
            string source = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index + 1}", overwrite: true);
        }
        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
