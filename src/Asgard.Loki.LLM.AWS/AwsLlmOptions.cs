namespace Asgard.Loki.LLM.AWS;

/// <summary>Bedrock 原生调用的区域和本地传输设置；不管理云资源。</summary>
public sealed record AwsLlmOptions
{
    /// <summary>明确的 SigV4 签名区域；不从主机、模型 ARN 或环境猜测。</summary>
    public required string Region { get; init; }
    /// <summary>Claude 正文版本；与调用者显式版本冲突时失败。</summary>
    public string ClaudeAnthropicVersion { get; init; } = "bedrock-2023-05-31";
    /// <summary>完整 JSON、错误或整次 EventStream 的原始字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>可选原生 x-amzn-bedrock-* 请求头；构造时独立快照，参与签名与 Session 绑定。</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; } = new Dictionary<string, string>();
    /// <summary>单帧分配的本地字节上限，不是服务端协议最大值。</summary>
    public int MaximumEventStreamFrameBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>单帧类型化头数量的本地解析上限，防止解码对象膨胀。</summary>
    public int MaximumEventStreamHeaders { get; init; } = 128;
    /// <summary>首个原生消息终态起的有限尾部暂存上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>Claude 原始内容块关联数上限，不按稀疏索引分配数组。</summary>
    public int MaximumTrackedClaudeBlocks { get; init; } = 128;
}
