namespace Talah.Harness.Adapters.Codex;

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message)
        : base(message)
    {
    }

    public CodexProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CodexRpcException(int code, string message) : Exception($"Codex App Server error {code}: {message}")
{
    public int Code { get; } = code;
}
