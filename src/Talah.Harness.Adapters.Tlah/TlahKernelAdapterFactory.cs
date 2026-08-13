using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.Tlah;

public sealed class TlahKernelAdapterFactory(Func<ITlahNativeRuntime> runtimeFactory) : IKernelAdapterFactory
{
    private readonly Func<ITlahNativeRuntime> _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

    public TlahKernelAdapterFactory()
        : this(static () => new TlahNativeRuntime())
    {
    }

    public string AdapterId => TlahKernelAdapter.Id;

    public ValueTask<IKernelAdapter> CreateAsync(KernelProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(profile.AdapterId, AdapterId, StringComparison.Ordinal))
            throw new ArgumentException($"Profile adapter id must be '{AdapterId}'.", nameof(profile));
        return ValueTask.FromResult<IKernelAdapter>(new TlahKernelAdapter(_runtimeFactory()));
    }
}
