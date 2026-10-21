using System.Net;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>完整原始正文及外层响应释放故障替身。</summary>
internal sealed class AudioResponseContent(byte[] bytes, Action? onDispose = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose?.Invoke(); }
}
