namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>借用式外部认证替身，只返回测试令牌，不读取环境或调用云端。</summary>
internal sealed class AzureTokenSource(AzureClock clock) : IAzureLlmTokenSource, IDisposable
{
    internal string Version { get; set; } = "source-v1";
    internal int Calls { get; private set; }
    internal List<string> Principals { get; } = [];
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, ValueTask<AzureLlmAccessToken>>? Resolve { get; set; }
    /// <inheritdoc />
    public string BindingIdentity => Version;
    /// <inheritdoc />
    public ValueTask<AzureLlmAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; Principals.Add(principalIdentity);
        return Resolve is not null ? Resolve(principalIdentity, cancellationToken) : ValueTask.FromResult(new AzureLlmAccessToken(principalIdentity, "fixture-token-" + Calls, clock.Now.AddHours(1)));
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
