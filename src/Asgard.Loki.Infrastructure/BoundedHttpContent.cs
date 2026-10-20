using System.Net;

namespace Asgard.Loki.Infrastructure;

/// <summary>一次性有界 HTTP 上传正文，包含 multipart framing 在内，不解释模型字段或重试。</summary>
public sealed class BoundedHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly long _maximumBytes;
    private readonly long? _expectedLength;
    private int _serialized;
    /// <summary>成功构造后拥有 inner 内容；其中借用的原始流仍由其独立所有权契约决定。</summary>
    public BoundedHttpContent(HttpContent inner, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(inner); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);
        if (inner.Headers.ContentLength > maximumBytes) throw new InvalidDataException("HTTP 上传正文的声明长度超过显式上限。");
        _inner = inner; _maximumBytes = maximumBytes; _expectedLength = inner.Headers.ContentLength;
        foreach (var field in inner.Headers) Headers.TryAddWithoutValidation(field.Key, field.Value);
    }
    /// <summary>仅使用内容已知长度，不消费输入来推断未知长度。</summary>
    protected override bool TryComputeLength(out long length) { length = _expectedLength ?? 0; return _expectedLength is not null; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <summary>按实际写入字节检查上限，拒绝处理器触发的重复序列化，不关闭借用的传输输出。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _serialized, 1) != 0) throw new InvalidOperationException("HTTP 上传正文不允许隐式重复序列化或重放。");
        using var bounded = new BoundedWriteStream(stream, _maximumBytes);
        await _inner.CopyToAsync(bounded, context, cancellationToken).ConfigureAwait(false);
        if (_expectedLength is { } expected && bounded.BytesWritten != expected) throw new InvalidDataException("HTTP 上传正文未匹配声明长度。");
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>只释放成功接管的内容包装，不取得其原始借用流的释放权。</summary>
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
