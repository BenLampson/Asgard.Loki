using System.Collections.Concurrent;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>只在测试进程内协调缓存绑定，不能用作生产分布式锁。</summary>
internal sealed class TestSessionLock(CancellationToken lostToken = default) : ILlmSessionLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    /// <inheritdoc />
    public async ValueTask<ILlmSessionLease> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new TestSessionLease(gate, lostToken);
    }
}
