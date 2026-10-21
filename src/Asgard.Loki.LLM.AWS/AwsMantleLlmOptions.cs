namespace Asgard.Loki.LLM.AWS;

/// <summary>Mantle 原生 Messages 的签名区域与本地传输策略，不管理云资源。</summary>
public sealed record AwsMantleLlmOptions
{
    /// <summary>明确 SigV4 区域；不会据此构造服务器或改写模型。</summary>
    public required string Region { get; init; }
    /// <summary>请求级 anthropic-version，不是 InvokeModel 的正文版本。</summary>
    public string AnthropicVersion { get; init; } = "2023-06-01";
    /// <summary>完整 JSON、错误或整次 SSE 的原始字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>从原生结束原因起暂存的终态/尾帧数量上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>原始 Claude 内容块关联数上限；不按稀疏索引分配数组。</summary>
    public int MaximumTrackedContentBlocks { get; init; } = 128;
    /// <summary>明确的 anthropic-*（版本除外）及 x-amzn-bedrock-* 原生头；独立快照、签名并参与绑定。</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; } = new Dictionary<string, string>();
}
