using System.Net;
using System.Text;

namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>校验响应头拒绝和响应内容释放，测试不读取真实服务。</summary>
internal sealed class GoogleEmbeddingTestContent(string json) : HttpContent
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(json);
    /// <summary>是否已读取正文。</summary>
    internal bool Read { get; private set; }
    /// <summary>是否已释放。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    { Read = true; return stream.WriteAsync(_bytes).AsTask(); }
    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
