namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>借用式认证替身，不读取环境或访问真实云端。</summary>
internal sealed class VertexTokenSource(VertexClock clock) : IGoogleVertexLlmTokenSource, IDisposable
{
    internal string Version { get; set; } = "vertex-source-v1";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, ValueTask<GoogleVertexLlmAccessToken>>? Resolve { get; set; }
    /// <inheritdoc />
    public string BindingIdentity => Version;
    /// <inheritdoc />
    public ValueTask<GoogleVertexLlmAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        return Resolve is not null ? Resolve(principalIdentity, cancellationToken) : ValueTask.FromResult(new GoogleVertexLlmAccessToken(principalIdentity, "fixture-token-" + Calls, clock.Now.AddHours(1)));
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
