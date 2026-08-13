namespace Talah.Harness.Adapters.OpenCode;

public static class OpenCodeRelease
{
    public const string Version = "1.18.9";
    public const string ReleaseTag = "v1.18.9";
    public const string WindowsX64Asset = "opencode-windows-x64.zip";
    public const string WindowsX64Sha256 = "1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2";
    public const string WindowsX64BaselineAsset = "opencode-windows-x64-baseline.zip";
    public const string WindowsX64BaselineSha256 = "0c85dc2d296417ac04dd51561985e5715f174a5ec38ae785d1f5233c3ebcc519";
    public const string ReleaseUrl = "https://github.com/anomalyco/opencode/releases/tag/v1.18.9";
    public const string OpenApiSchemaFile = "openapi-1.18.9.json";
    public static readonly Version MinimumCompatibleVersion = new(1, 18, 9);
    public static readonly Version MaximumCompatibleVersionExclusive = new(1, 19, 0);
}
