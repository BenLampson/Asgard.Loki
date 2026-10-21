namespace Asgard.Loki.LLM.Tests;

/// <summary>仅有协议声明而无适配接口，证明声明不能伪造实现。</summary>
internal sealed class ProtocolClaimOnlyProvider : ILlmProvider, ILlmProtocolSupport
{
    /// <inheritdoc />
    public string Id => "claim-only";
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) => true;
}
