namespace Asgard.Loki.LLM;

/// <summary>表达共享协调租约的有效期与安全释放。</summary>
public interface ILlmSessionLease : IAsyncDisposable
{
    /// <summary>租约丢失时取消；存储操作必须响应该通知。</summary>
    CancellationToken LostToken { get; }
}
