using Talah.Harness.Contracts;
using Talah.Harness.Runtime;

namespace Talah.Harness.Adapters.Codex;

public sealed class CodexAdapterFactory : IKernelAdapterFactory
{
    public string AdapterId => CodexKernelAdapter.CodexAdapterId;

    public async ValueTask<IKernelAdapter> CreateAsync(
        KernelProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(profile.AdapterId, AdapterId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Profile adapter must be '{AdapterId}'.", nameof(profile));
        }

        string executable = profile.Environment.TryGetValue("TALAH_CODEX_PATH", out string? configured)
            ? configured
            : "codex";
        string codexHome = Path.Combine(profile.DataRoot, "codex-home");
        DpapiCredentialStore credentials = ProfileCredentialStore.ForProfile(profile);
        string? secret = await credentials.GetAsync(
            profile.AdapterId,
            profile.ProfileId,
            ProfileCredentialStore.DeepSeekApiKeyCredentialId,
            cancellationToken).ConfigureAwait(false);
        bool deepSeekConfigured = !string.IsNullOrEmpty(secret);
        await CodexDeepSeekConfiguration.PrepareHomeAsync(
            codexHome,
            profile.Environment.GetValueOrDefault("TALAH_CODEX_MODEL"),
            deepSeekConfigured,
            cancellationToken).ConfigureAwait(false);

        var environment = new Dictionary<string, string>(profile.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["CODEX_HOME"] = codexHome
        };
        if (deepSeekConfigured)
            environment[KernelCredentialEnvironment.DeepSeekApiKey] = secret!;

        var transport = CodexProcessTransport.Start(executable, environment);
        var client = new CodexAppServerClient(transport);
        return new CodexKernelAdapter(profile, client);
    }
}
