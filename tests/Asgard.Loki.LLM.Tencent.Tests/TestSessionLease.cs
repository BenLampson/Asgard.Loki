using Asgard.Loki.LLM;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>以相同租约契约替代分布式协调，不访问外部服务。</summary>
internal sealed class TestSessionLease(SemaphoreSlim gate, CancellationToken lostToken = default) : ILlmSessionLease
{
    /// <inheritdoc />
    public CancellationToken LostToken => lostToken;
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        gate.Release();
        return ValueTask.CompletedTask;
    }
}
