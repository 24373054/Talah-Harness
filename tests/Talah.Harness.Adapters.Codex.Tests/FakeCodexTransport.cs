using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Talah.Harness.Adapters.Codex.Tests;

internal sealed class FakeCodexTransport : ICodexTransport
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _error = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<JsonElement> _sent = Channel.CreateUnbounded<JsonElement>();
    private readonly CallbackWriter _writer;
    private readonly ChannelReaderTextReader _outputReader;
    private readonly ChannelReaderTextReader _errorReader;

    public FakeCodexTransport(Func<JsonElement, Task>? onSent = null)
    {
        OnSent = onSent;
        _writer = new CallbackWriter(ReceiveFromClientAsync);
        _outputReader = new ChannelReaderTextReader(_output.Reader);
        _errorReader = new ChannelReaderTextReader(_error.Reader);
    }

    public Func<JsonElement, Task>? OnSent { get; set; }

    public TextWriter Input => _writer;

    public TextReader Output => _outputReader;

    public TextReader Error => _errorReader;

    public Task Completion => _completion.Task;

    public void Send(object message) => _output.Writer.TryWrite(JsonSerializer.Serialize(message));

    public void SendRaw(string line) => _output.Writer.TryWrite(line);

    public void SendError(string line) => _error.Writer.TryWrite(line);

    public async Task<JsonElement> NextSentAsync(CancellationToken cancellationToken = default) =>
        await _sent.Reader.ReadAsync(cancellationToken);

    public void Exit()
    {
        _output.Writer.TryComplete();
        _error.Writer.TryComplete();
        _completion.TrySetResult();
    }

    private async Task ReceiveFromClientAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var value = document.RootElement.Clone();
        await _sent.Writer.WriteAsync(value);
        if (OnSent is not null)
        {
            await OnSent(value);
        }
    }

    public ValueTask DisposeAsync()
    {
        Exit();
        _sent.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private sealed class CallbackWriter : TextWriter
    {
        private readonly Func<string, Task> _callback;

        public CallbackWriter(Func<string, Task> callback)
        {
            _callback = callback;
        }

        public override Encoding Encoding => new UTF8Encoding(false);

        public override Task WriteLineAsync(string? value) => _callback(value ?? string.Empty);

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _callback(buffer.ToString());
        }

        public override Task FlushAsync() => Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChannelReaderTextReader : TextReader
    {
        private readonly ChannelReader<string> _reader;

        public ChannelReaderTextReader(ChannelReader<string> reader)
        {
            _reader = reader;
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }
}
