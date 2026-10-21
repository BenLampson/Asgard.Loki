namespace Asgard.Loki.Embedding.Tests;

/// <summary>用于独立核心契约测试的自定义原生适配器，不访问网络或模拟真实向量算法。</summary>
internal sealed class StubEmbeddingAdapter(string id, Func<EmbeddingRequest, CancellationToken, Task<EmbeddingResult>>? call = null)
    : IEmbeddingAdapter, IDisposable
{
    private int _calls;
    /// <inheritdoc />
    public string Id { get; } = id;
    /// <summary>实际适配调用次数。</summary>
    internal int Calls => _calls;
    /// <summary>资源所有权检查标记。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return call is null ? Task.FromResult(new EmbeddingResult(System.Text.Json.JsonSerializer.SerializeToElement(new { provider = Id }),
            new(request.Model, "native-" + Id, Id))) : call(request, cancellationToken);
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
