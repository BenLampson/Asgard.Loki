namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>显式的离线主体和令牌断言，观察来源变化及借用所有权。</summary>
internal sealed class VertexTestTokenSource : IGoogleVertexImageTokenSource, IDisposable
{
    public string BindingIdentity { get; set; } = "fixture-source";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal Func<CancellationToken, ValueTask<GoogleVertexImageAccessToken>> OnGet { get; set; }
        = _ => ValueTask.FromResult(new GoogleVertexImageAccessToken("fixture-principal", "fixture-token-one", new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    public ValueTask<GoogleVertexImageAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default)
    { if (principalIdentity != "fixture-principal") throw new InvalidOperationException("测试主体不符。"); Calls++; return OnGet(cancellationToken); }
    public void Dispose() => Disposed = true;
}
