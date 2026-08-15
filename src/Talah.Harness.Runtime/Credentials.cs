using System.Security.Cryptography;
using System.Text;

namespace Talah.Harness.Runtime;

public sealed class DpapiCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Talah.Harness.Credential.v1");
    private readonly ProfilePathProvider? _paths;
    private readonly string? _credentialRoot;

    public DpapiCredentialStore(ProfilePathProvider paths)
        : this(paths, null)
    {
    }

    public DpapiCredentialStore(string credentialRoot)
        : this(null, credentialRoot)
    {
    }

    private DpapiCredentialStore(ProfilePathProvider? paths, string? credentialRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI CurrentUser credentials require Windows.");
        if (paths is null && string.IsNullOrWhiteSpace(credentialRoot))
            throw new ArgumentException("A credential path provider or credential root is required.", nameof(paths));
        _paths = paths;
        _credentialRoot = string.IsNullOrWhiteSpace(credentialRoot) ? null : Path.GetFullPath(credentialRoot);
    }

    public async Task SetAsync(string adapterId, string profileId, string credentialId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ProfilePathProvider.ValidateIdentifier(credentialId, nameof(credentialId));
        string target = GetPath(adapterId, profileId, credentialId, create: true);
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        try
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async Task<string?> GetAsync(string adapterId, string profileId, string credentialId, CancellationToken cancellationToken = default)
    {
        ProfilePathProvider.ValidateIdentifier(credentialId, nameof(credentialId));
        string target = GetPath(adapterId, profileId, credentialId, create: false);
        if (!File.Exists(target)) return null;
        byte[] protectedBytes = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The stored credential cannot be decrypted for the current Windows user.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task<bool> DeleteAsync(string adapterId, string profileId, string credentialId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProfilePathProvider.ValidateIdentifier(credentialId, nameof(credentialId));
        string target = GetPath(adapterId, profileId, credentialId, create: false);
        if (!File.Exists(target)) return Task.FromResult(false);
        File.Delete(target);
        return Task.FromResult(true);
    }

    private string GetPath(string adapterId, string profileId, string credentialId, bool create)
    {
        string root = _paths is not null
            ? _paths.GetCredentialRoot(adapterId, profileId, create)
            : _credentialRoot!;
        if (create && _paths is null) Directory.CreateDirectory(root);
        return Path.Combine(root, credentialId + ".dpapi");
    }
}

public sealed class SecretRedactor
{
    private readonly string[] _secrets;
    private readonly string _replacement;

    public SecretRedactor(IEnumerable<string> secrets, string replacement = "[REDACTED]")
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = [.. secrets.Where(static value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderByDescending(static value => value.Length)];
        _replacement = replacement;
    }

    public string Redact(string? value)
    {
        if (value is null) return string.Empty;
        foreach (string secret in _secrets) value = value.Replace(secret, _replacement, StringComparison.Ordinal);
        return value;
    }
}
