using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>HTTP/JSON 与 SSE 的本地资源策略，不声明模型能力或执行重试。</summary>
public sealed record OpenAiHttpOptions
{
    /// <summary>单次 JSON、错误或整个 SSE 的原始响应字节上限；超限失败，不截断。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>第一个可能终态起暂存的事件数量上限，包含用量及未知尾帧。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>原生 Moderations 请求 UTF-8 字节上限；省略有效为 32 MiB，省略字段保持已有绑定字节。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumModerationRequestBytes { get; init; }

    /// <summary>原生文本补全请求 UTF-8 字节上限；省略有效为 32 MiB，默认不改变既有绑定字节。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumTextCompletionRequestBytes { get; init; }
    /// <summary>原生文本补全流中不同 choice 的数量上限；省略有效为 128，不按索引大小分配。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumTrackedTextChoices { get; init; }

}
