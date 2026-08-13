using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Talah.Harness.Runtime;

public sealed partial class ProfilePathProvider
{
    private readonly string _productRoot;
    private readonly bool _hardenAcl;

    public ProfilePathProvider(string productRoot, bool hardenAcl = true)
    {
        if (!Path.IsPathFullyQualified(productRoot)) throw new ArgumentException("The product root must be absolute.", nameof(productRoot));
        _productRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
        _hardenAcl = hardenAcl;
    }

    public string ProductRoot => _productRoot;

    public string GetProfileRoot(string adapterId, string profileId, bool create = true)
    {
        ValidateIdentifier(adapterId, nameof(adapterId));
        ValidateIdentifier(profileId, nameof(profileId));
        var result = Path.GetFullPath(Path.Combine(_productRoot, "profiles", adapterId, profileId));
        EnsureContained(result);
        if (create)
        {
            Directory.CreateDirectory(result);
            if (_hardenAcl) TryHardenDirectory(result);
        }

        return result;
    }

    public string GetCredentialRoot(string adapterId, string profileId, bool create = true)
    {
        var profileRoot = GetProfileRoot(adapterId, profileId, create);
        var result = Path.Combine(profileRoot, ".credentials");
        if (create)
        {
            Directory.CreateDirectory(result);
            if (_hardenAcl) TryHardenDirectory(result);
        }

        return result;
    }

    public static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value is "." or ".." || !IdentifierPattern().IsMatch(value))
            throw new ArgumentException("Identifiers may contain only ASCII letters, digits, '.', '_', and '-' and must be 1-80 characters.", parameterName);
    }

    private void EnsureContained(string path)
    {
        var prefix = _productRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The resolved profile path is outside the configured product root.");
    }

    private static void TryHardenDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null) return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

public sealed record ProfileMetadata(
    string AdapterId,
    string ProfileId,
    string DisplayName,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class ProfileMetadataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProfilePathProvider _paths;

    public ProfileMetadataStore(ProfilePathProvider paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<ProfileMetadata> UpsertAsync(ProfileMetadata profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfilePathProvider.ValidateIdentifier(profile.AdapterId, nameof(profile.AdapterId));
        ProfilePathProvider.ValidateIdentifier(profile.ProfileId, nameof(profile.ProfileId));
        if (string.IsNullOrWhiteSpace(profile.DisplayName)) throw new ArgumentException("A display name is required.", nameof(profile));
        var root = _paths.GetProfileRoot(profile.AdapterId, profile.ProfileId);
        var target = Path.Combine(root, "profile.json");
        await using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, profile, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<ProfileMetadata?> GetAsync(string adapterId, string profileId, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(_paths.GetProfileRoot(adapterId, profileId, create: false), "profile.json");
        if (!File.Exists(target)) return null;
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<ProfileMetadata>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Profile metadata is empty or invalid.");
    }

    public async Task<IReadOnlyList<ProfileMetadata>> ListAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        ProfilePathProvider.ValidateIdentifier(adapterId, nameof(adapterId));
        var adapterRoot = Path.Combine(_paths.ProductRoot, "profiles", adapterId);
        if (!Directory.Exists(adapterRoot)) return [];
        var result = new List<ProfileMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(adapterRoot, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "profile.json");
            if (!File.Exists(path)) continue;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var profile = await JsonSerializer.DeserializeAsync<ProfileMetadata>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (profile is not null) result.Add(profile);
        }

        return result;
    }

    public Task<bool> DeleteAsync(string adapterId, string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.Combine(_paths.GetProfileRoot(adapterId, profileId, create: false), "profile.json");
        if (!File.Exists(target)) return Task.FromResult(false);
        File.Delete(target);
        return Task.FromResult(true);
    }
}
