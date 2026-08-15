using Talah.Harness.Contracts;

namespace Talah.Harness.Runtime;

public static class ProfileCredentialStore
{
    public const string DeepSeekApiKeyCredentialId = "deepseek-api-key";

    public static DpapiCredentialStore ForProfile(KernelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string root = Path.GetFullPath(profile.DataRoot);
        string expectedSuffix = Path.Combine("profiles", profile.AdapterId, profile.ProfileId);
        if (root.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            string productRoot = Path.GetFullPath(Path.Combine(root, "..", "..", ".."));
            return new DpapiCredentialStore(new ProfilePathProvider(productRoot));
        }

        return new DpapiCredentialStore(Path.Combine(root, ".credentials"));
    }
}
