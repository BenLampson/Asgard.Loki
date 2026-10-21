namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>借用凭据来源替身，区分稳定配置/主体和轮换短期 Key。</summary>
internal sealed class HunyuanSource : ITencentHunyuanCredentialSource, IDisposable
{
    public string BindingIdentity { get; set; } = "fixture-source";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal TencentHunyuanCredentials Credentials { get; set; } = new("fixture-tencent-principal", "AKIDFIXTURE000000000", "fixture-tencent-signing-key", DateTimeOffset.MaxValue);
    internal Func<CancellationToken, ValueTask<TencentHunyuanCredentials>>? Acquire { get; set; }
    public ValueTask<TencentHunyuanCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default)
    { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Acquire is null ? ValueTask.FromResult(Credentials) : Acquire(cancellationToken); }
    public void Dispose() => Disposed = true;
}
