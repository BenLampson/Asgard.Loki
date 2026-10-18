namespace Asgard.Loki.LLM;

/// <summary>保留显式转换来源及损耗证据，不改变原生结果契约。</summary>
public sealed class LlmConversionInfo
{
    /// <summary>复制上游数据与诊断，避免调用方或文档释放影响结果。</summary>
    public LlmConversionInfo(LlmProtocol upstreamProtocol, LlmProtocol publicProtocol, JsonElement upstreamData,
        IEnumerable<LlmConversionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        UpstreamProtocol = upstreamProtocol;
        PublicProtocol = publicProtocol;
        UpstreamData = upstreamData.Clone();
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    /// <summary>实际调用的上游协议。</summary>
    public LlmProtocol UpstreamProtocol { get; }
    /// <summary>调用者选择的公共协议。</summary>
    public LlmProtocol PublicProtocol { get; }
    /// <summary>完整原生响应或当前上游事件快照。</summary>
    public JsonElement UpstreamData { get; }
    /// <summary>请求及响应转换累计诊断。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics { get; }
}
