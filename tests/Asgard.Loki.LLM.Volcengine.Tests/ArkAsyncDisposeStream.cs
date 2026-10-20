namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>仅异步关闭失败的真实内存流，用于区分读取完成与资源关闭完成。</summary>
internal sealed class ArkAsyncDisposeStream(byte[] bytes) : MemoryStream(bytes)
{
    internal bool AsyncDisposed { get; private set; }
    public override ValueTask DisposeAsync()
    { AsyncDisposed = true; base.Dispose(); throw new IOException("async close"); }
}
