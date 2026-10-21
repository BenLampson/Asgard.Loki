namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>借用认证源替身，不读取环境、登录或访问网络。</summary>
internal sealed class VertexVideoTokenSource(VertexVideoClock clock) : IGoogleVertexVideoTokenSource, IDisposable
{
    internal string Version { get; set; } = "video-token-source-v1";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, ValueTask<GoogleVertexVideoAccessToken>>? Resolve { get; set; }
    /// <inheritdoc />
    public string BindingIdentity => Version;
    /// <inheritdoc />
    public ValueTask<GoogleVertexVideoAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        return Resolve is null ? ValueTask.FromResult(new GoogleVertexVideoAccessToken(principalIdentity, "fixture-token-" + Calls, clock.Now.AddHours(1))) : Resolve(principalIdentity, cancellationToken);
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
