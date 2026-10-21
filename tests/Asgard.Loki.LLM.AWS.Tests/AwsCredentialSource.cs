namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>可轮换的借用来源替身，全部 Key 均为 AWS 公布的非真实示例。</summary>
internal sealed class AwsCredentialSource(AwsClock clock) : IAwsLlmCredentialSource, IDisposable
{
    internal const string KeyId = "AKIDEXAMPLE";
    internal const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    internal string Version { get; set; } = "fixture-source-v1";
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, ValueTask<AwsLlmSigningCredentials>>? Resolve { get; set; }
    /// <inheritdoc />
    public string BindingIdentity => Version;
    /// <inheritdoc />
    public ValueTask<AwsLlmSigningCredentials> GetCredentialsAsync(string principalIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; return Resolve is null ? ValueTask.FromResult(new AwsLlmSigningCredentials(principalIdentity, KeyId, Secret, clock.Now.AddHours(1))) : Resolve(principalIdentity, cancellationToken);
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
