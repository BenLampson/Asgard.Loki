namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>同步关闭成功而异步关闭失败，防止成功尾部跳过实际 DisposeAsync。</summary>
internal sealed class MiniMaxAsyncFaultStream(byte[] bytes) : MemoryStream(bytes)
{
    internal bool ClosedAsync { get; private set; }
    public override ValueTask DisposeAsync() { ClosedAsync = true; Dispose(); return ValueTask.FromException(new IOException("async source close failure")); }
}
