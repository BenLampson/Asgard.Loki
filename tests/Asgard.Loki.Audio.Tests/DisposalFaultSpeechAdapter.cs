namespace Asgard.Loki.Audio.Tests;

/// <summary>在 MoveNext 正常结束后由真正的 DisposeAsync 抛错，验证核心的终态发布顺序。</summary>
internal sealed class DisposalFaultSpeechAdapter(IReadOnlyList<SpeechChunk> chunks)
    : INativeSpeechAdapter, IAsyncEnumerable<SpeechChunk>, IAsyncEnumerator<SpeechChunk>
{
    private int _index = -1;
    private CancellationToken _token;
    /// <inheritdoc />
    public string Id => "dispose-fault";
    /// <summary>真实源枚举释放次数。</summary>
    internal int DisposeCalls { get; private set; }
    /// <inheritdoc />
    public SpeechChunk Current => chunks[_index];
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default) => this;
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default) => this;
    /// <inheritdoc />
    public IAsyncEnumerator<SpeechChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    { _index = -1; _token = cancellationToken; return this; }
    /// <inheritdoc />
    public ValueTask<bool> MoveNextAsync()
    { _token.ThrowIfCancellationRequested(); return ValueTask.FromResult(++_index < chunks.Count); }
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    { DisposeCalls++; return ValueTask.FromException(new IOException("source DisposeAsync failed")); }
}
