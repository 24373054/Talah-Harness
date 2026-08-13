using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Codex;

public sealed class CodexAdapterFactory : IKernelAdapterFactory
{
    public string AdapterId => CodexKernelAdapter.CodexAdapterId;

    public ValueTask<IKernelAdapter> CreateAsync(
        KernelProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(profile.AdapterId, AdapterId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Profile adapter must be '{AdapterId}'.", nameof(profile));
        }

        var executable = profile.Environment.TryGetValue("TALAH_CODEX_PATH", out var configured)
            ? configured
            : "codex";
        var transport = CodexProcessTransport.Start(executable, profile.Environment);
        var client = new CodexAppServerClient(transport);
        return ValueTask.FromResult<IKernelAdapter>(new CodexKernelAdapter(profile, client));
    }
}
