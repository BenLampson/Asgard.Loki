namespace Asgard.Loki.Image;

/// <summary>线程安全轮询完整图片目标，不因参数或调用失败重新选择。</summary>
public sealed class RoundRobinImageProviderSelector : ILokiImageProviderSelector
{
    private int _sequence = -1;
    /// <inheritdoc />
    public int Select(string model, IReadOnlyList<LokiImageUpstreamProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentOutOfRangeException.ThrowIfZero(providers.Count);
        return (int)((uint)Interlocked.Increment(ref _sequence) % (uint)providers.Count);
    }
}
