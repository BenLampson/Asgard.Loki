using System.Net;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>以本地响应内容注入外层释放错误或取消。</summary>
internal sealed class V1Content(byte[] bytes, Action? onDispose = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose?.Invoke(); }
}
