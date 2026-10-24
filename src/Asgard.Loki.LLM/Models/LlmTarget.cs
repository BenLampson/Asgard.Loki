namespace Asgard.Loki.LLM;

/// <summary>完整定义一个可调度的上游调用目标。</summary>
public sealed record LlmTarget
{
    /// <summary>跨配置保持稳定的目标标识。</summary>
    public required string Id { get; init; }
    /// <summary>注册的供应商实现标识。</summary>
    public required string ProviderId { get; init; }
    /// <summary>包含 API 基础路径的地址，例如方舟的 /api/v3/。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>上游实际模型或推理接入点。</summary>
    public required string UpstreamModel { get; init; }
    /// <summary>引用的凭据身份。</summary>
    public required string CredentialId { get; init; }
    /// <summary>供应商实现或连接语义变化时必须更新的配置版本。</summary>
    public string Version { get; init; } = "1";
    /// <summary>由配置明确声明的协议能力。</summary>
    public LlmProtocol Protocols { get; init; } = LlmProtocol.ChatCompletions | LlmProtocol.Responses;
    /// <summary>由配置明确启用的可选操作；默认省略以保持旧基础目标的指纹。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LlmTargetFeatures Features { get; init; }
    /// <summary>按调用协议限制显式输出格式（text/json_object/json_schema）；null 或缺少协议不限制，空列表拒绝显式格式。注册时复制，参与 Session 绑定，不查询模型或自动降级。</summary>
    /// <remarks>支持 ChatCompletions、Responses、ClaudeMessages、Gemini、OllamaChat/Generate、BedrockConverse、CohereChat、WorkersAiText 的原生格式字段；其他协议键注册失败。转换适配器的实际输出另由转换包 TargetSettings 约束。</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<LlmProtocol, IReadOnlyList<string>>? AllowedOutputFormats { get; init; }
}
