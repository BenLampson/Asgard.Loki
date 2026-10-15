namespace Asgard.Loki.LLM;

/// <summary>协调同一缓存键的首次绑定；多实例实现必须共享协调状态。</summary>
public interface ILlmSessionLock
{
    /// <summary>取得独占租约，失败时抛出异常，不能返回无效句柄。</summary>
    ValueTask<ILlmSessionLease> AcquireAsync(string key, CancellationToken cancellationToken = default);
}
