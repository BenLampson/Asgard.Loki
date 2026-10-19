using System.Runtime.CompilerServices;

namespace Asgard.Loki.Audio.Tests;

/// <summary>记录原生 JSON 及枚举释放，测试音频核心而不实现供应商协议。</summary>
internal sealed class StubNativeSpeechAdapter(IEnumerable<SpeechChunk> chunks) : INativeSpeechAdapter, IDisposable
{
    /// <inheritdoc />
    public string Id { get; init; } = "native";
    /// <summary>完整目标实际调用次数。</summary>
    internal int Calls { get; private set; }
    /// <summary>收到的独立原生请求。</summary>
    internal NativeSpeechRequest? LastRequest { get; private set; }
    /// <summary>是否已释放源枚举。</summary>
    internal bool IteratorDisposed { get; private set; }
    /// <summary>仅由持有者释放；核心模块不得接管此标记。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>派发时可观察或修改调用者源对象。</summary>
    internal Action<NativeSpeechRequest>? OnRequest { get; init; }
    /// <summary>完成块后的确定性回调。</summary>
    internal Action? OnTail { get; init; }
    /// <summary>完成块后模拟读取失败。</summary>
    internal Exception? TailError { get; init; }
    /// <inheritdoc />
    public IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);
    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); LastRequest = request; OnRequest?.Invoke(request);
        await foreach (var chunk in ReadAsync(cancellationToken)) yield return chunk;
    }
    /// <summary>完成块后仍可能发生错误，核心必须先完整观察源尾部再发成功终态。</summary>
    private async IAsyncEnumerable<SpeechChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this); cancellationToken.ThrowIfCancellationRequested(); Calls++; IteratorDisposed = false;
        try
        {
            foreach (var chunk in chunks)
            { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return chunk; }
            OnTail?.Invoke(); if (TailError is not null) throw TailError;
        }
        finally { IteratorDisposed = true; }
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
