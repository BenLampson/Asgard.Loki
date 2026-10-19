using System.Net;
using System.Text;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>跟踪成功和异常路径的响应内容释放，不访问网络。</summary>
internal sealed class TrackedEmbeddingContent(string json) : HttpContent
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(json);
    /// <summary>响应内容是否已释放。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>是否开始读取正文。</summary>
    internal bool Serialized { get; private set; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    { Serialized = true; return stream.WriteAsync(_bytes).AsTask(); }
    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
