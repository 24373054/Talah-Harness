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

public sealed class CodexRpcException : Exception
{
    public CodexRpcException(int code, string message)
        : base($"Codex App Server error {code}: {message}")
    {
        Code = code;
    }

    public int Code { get; }
}
