namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>借用式主体/Key 来源替身；稳定配置与轮换认证材料分别控制。</summary>
internal sealed class SparkSource : IXunfeiSparkCredentialSource, IDisposable
{
    public string BindingIdentity { get; set; } = "fixture-spark-source";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal XunfeiSparkCredentials Credentials { get; set; } = new("fixture-spark-principal", "fixture-spark-key", "fixture-spark-secret", DateTimeOffset.MaxValue);
    internal Func<CancellationToken, ValueTask<XunfeiSparkCredentials>>? Acquire { get; set; }
    public ValueTask<XunfeiSparkCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default)
    { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Acquire is null ? ValueTask.FromResult(Credentials) : Acquire(cancellationToken); }
    public void Dispose() => Disposed = true;
}
