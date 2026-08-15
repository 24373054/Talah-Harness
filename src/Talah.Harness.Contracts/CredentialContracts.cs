namespace Talah.Harness.Contracts;

public static class KernelCredentialEnvironment
{
    public const string DeepSeekApiKey = "DEEPSEEK_API_KEY";
}

public interface ICredentialChangeRequiresRestart
{
    bool RequiresRestartAfterCredentialChange { get; }
}
