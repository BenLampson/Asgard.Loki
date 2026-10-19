namespace Asgard.Loki.Rerank.Internal;

/// <summary>单一路由内的不可变目标集合与线程安全轮询状态，不保存跨调用会话。</summary>
internal sealed class RerankRouteState(IRerankAdapter[] targets)
{
    private int _sequence = -1;

    /// <summary>每次只选择一个完整目标；无自动重试、切换或响应后的二次选择。</summary>
    internal IRerankAdapter Select() => targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
}
