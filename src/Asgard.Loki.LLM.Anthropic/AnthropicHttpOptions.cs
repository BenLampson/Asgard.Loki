using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.Anthropic;

/// <summary>Messages 的显式认证和本地传输资源策略，不管理云身份或模型能力。</summary>
public sealed record AnthropicHttpOptions
{
    /// <summary>选择唯一认证方式；其他认证种类不能借用默认头或自动降级。</summary>
    public AnthropicAuthentication Authentication { get; init; }
    /// <summary>完整 JSON、错误或整个 SSE 的原始响应字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>从原生结束原因或错误开始暂存的事件数量上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>单次流原始内容块关联数上限；不按稀疏索引分配数组。</summary>
    public int MaximumTrackedContentBlocks { get; init; } = 128;
    /// <summary>可选原生相对路径与凭据格式快照；省略保持现有 ApiKey/Bearer 传输和绑定字节。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicHttpTransportOptions? Transport { get; init; }
}
