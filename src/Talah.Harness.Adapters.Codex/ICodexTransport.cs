namespace Talah.Harness.Adapters.Codex;

internal interface ICodexTransport : IAsyncDisposable
{
    TextWriter Input { get; }

    TextReader Output { get; }

    TextReader Error { get; }

    Task Completion { get; }

    void Abort();
}
