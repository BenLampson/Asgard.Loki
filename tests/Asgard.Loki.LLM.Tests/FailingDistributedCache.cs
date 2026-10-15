using Microsoft.Extensions.Caching.Distributed;

namespace Asgard.Loki.LLM.Tests;

/// <summary>模拟共享缓存不可用，检查是否存在静默回退。</summary>
internal sealed class FailingDistributedCache : IDistributedCache
{
    /// <inheritdoc />
    public byte[]? Get(string key) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public void Refresh(string key) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public void Remove(string key) => throw new IOException("Cache unavailable.");
    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default) => throw new IOException("Cache unavailable.");
}
