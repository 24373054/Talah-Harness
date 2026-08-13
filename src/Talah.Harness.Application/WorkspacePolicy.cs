using System.Security.Cryptography;
using System.Text;
using Talah.Harness.Contracts;

namespace Talah.Harness.Application;

public sealed class WorkspacePolicy
{
    public static WorkspaceDescriptor CreateDescriptor(
        string rootPath,
        IEnumerable<string>? additionalRoots = null,
        bool isTrusted = false)
    {
        string normalizedRoot = NormalizeExistingDirectory(rootPath);
        string[] normalizedAdditional = [.. (additionalRoots ?? [])
            .Select(NormalizeExistingDirectory)
            .Where(path => !string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        string identityMaterial = string.Join('\n', normalizedAdditional.Prepend(normalizedRoot));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial)))[..20].ToLowerInvariant();
        return new WorkspaceDescriptor($"ws_{digest}", normalizedRoot, normalizedAdditional, isTrusted);
    }

    public WorkspaceDescriptor Normalize(WorkspaceDescriptor workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return CreateDescriptor(workspace.RootPath, workspace.AdditionalRoots, workspace.IsTrusted);
    }

    public void ValidateReferencedPaths(WorkspaceDescriptor workspace, IEnumerable<string>? referencedPaths)
    {
        if (referencedPaths is null) return;
        WorkspaceDescriptor normalized = Normalize(workspace);
        string[] roots = normalized.AdditionalRoots.Prepend(normalized.RootPath).ToArray();
        foreach (string path in referencedPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Referenced paths cannot be empty.", nameof(referencedPaths));
            string candidate = Path.IsPathFullyQualified(path)
                ? ResolvePath(path)
                : ResolvePath(Path.Combine(normalized.RootPath, path));
            if (!roots.Any(root => IsContained(root, candidate)))
            {
                throw new UnauthorizedAccessException($"The referenced path '{path}' resolves outside every approved workspace root.");
            }
        }
    }

    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Workspace roots must be non-empty absolute paths.", nameof(path));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Workspace root '{path}' does not exist.");
        return Path.TrimEndingDirectorySeparator(ResolvePath(path));
    }

    private static string ResolvePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("The path has no filesystem root.", nameof(path));
        string relative = fullPath[root.Length..];
        string[] segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string current = Path.TrimEndingDirectorySeparator(root);
        foreach (string segment in segments)
        {
            string candidate = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            if (entry is not null)
            {
                FileSystemInfo? resolved = entry.ResolveLinkTarget(returnFinalTarget: true);
                current = resolved?.FullName ?? entry.FullName;
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }

    private static bool IsContained(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
