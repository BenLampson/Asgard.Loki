using System.Net;

namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>注入完整原始正文及响应内容释放边界。</summary>
internal sealed class SpeechContent(byte[] bytes, Action? onDispose = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose?.Invoke(); }
}
