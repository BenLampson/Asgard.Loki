namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>可观测的一次取令牌替身，不读环境、认证文件或网络。</summary>
internal sealed class CodexFixtureSource : ICodexAccessTokenSource, IDisposable
{
    public string BindingIdentity { get; set; } = "fixture-source-v1";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, ValueTask<CodexAccessToken>>? Handler { get; init; }
    public ValueTask<CodexAccessToken> GetTokenAsync(string principalIdentity, CancellationToken cancellationToken = default)
    {
        Calls++; return Handler is null ? ValueTask.FromResult(new CodexAccessToken(principalIdentity, CodexFixture.Part("account_id").GetString()!, CodexFixture.Token, CodexFixture.Now.AddHours(1))) : Handler(principalIdentity, cancellationToken);
    }
    public void Dispose() => Disposed = true;
}
