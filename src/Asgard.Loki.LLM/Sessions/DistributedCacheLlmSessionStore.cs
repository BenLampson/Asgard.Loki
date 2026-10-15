using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Asgard.Loki.LLM;

/// <summary>通过标准缓存抽象保存绑定，借用外部协调器保障首次绑定互斥。</summary>
public sealed class DistributedCacheLlmSessionStore : ILlmSessionStore
{
    private readonly IDistributedCache _cache;
    private readonly ILlmSessionLock _sessionLock;
    private readonly string _prefix;
    private readonly TimeSpan _expiration;

    /// <summary>传入缓存与协调器；测试可使用内存缓存，多实例需使用共享分布式协调器。</summary>
    public DistributedCacheLlmSessionStore(IDistributedCache cache, ILlmSessionLock sessionLock,
        TimeSpan? expiration = null, string keyPrefix = "Asgard:Loki:LLM:")
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sessionLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _cache = cache;
        _sessionLock = sessionLock;
        _prefix = keyPrefix;
        _expiration = expiration ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_expiration, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public async ValueTask<LlmSessionBinding?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var value = await _cache.GetAsync(SessionKey(sessionId), cancellationToken).ConfigureAwait(false);
        return value is null ? null : JsonSerializer.Deserialize<LlmSessionBinding>(value)
            ?? throw new InvalidDataException("会话缓存不是有效的绑定。");
    }

    /// <inheritdoc />
    public async ValueTask<LlmSessionBinding> GetOrCreateAsync(LlmSessionBinding candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var key = SessionKey(candidate.SessionId);
        await using var lease = await _sessionLock.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LostToken);
        var existing = await GetAsync(candidate.SessionId, operation.Token).ConfigureAwait(false);
        if (existing is not null) return existing;
        operation.Token.ThrowIfCancellationRequested();
        await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(candidate), EntryOptions(), operation.Token).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        return candidate;
    }

    /// <inheritdoc />
    public async ValueTask BindResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await _cache.SetStringAsync(ResponseKey(binding.SessionId, responseId), binding.Fingerprint,
            EntryOptions(), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(binding.SessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> OwnsResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var key = ResponseKey(binding.SessionId, responseId);
        var fingerprint = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(fingerprint, binding.Fingerprint, StringComparison.Ordinal)) return false;
        await _cache.RefreshAsync(key, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask RefreshAsync(string sessionId, CancellationToken cancellationToken = default)
        => await _cache.RefreshAsync(SessionKey(sessionId), cancellationToken).ConfigureAwait(false);

    /// <summary>仅通过共享缓存管理有效期。</summary>
    private DistributedCacheEntryOptions EntryOptions() => new() { SlidingExpiration = _expiration };
    /// <summary>隔离逻辑会话键，避免原始会话标识进入缓存键。</summary>
    private string SessionKey(string sessionId) => _prefix + "session:" + Hash(sessionId);
    /// <summary>响应记录按会话隔离，允许保留多个分支。</summary>
    private string ResponseKey(string sessionId, string responseId) => _prefix + "response:" + Hash(sessionId) + ":" + Hash(responseId);
    /// <summary>为不透明标识创建固定长度键。</summary>
    private static string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
