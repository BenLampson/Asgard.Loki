namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Vertex 请求身份与本地资源策略，不管理云端项目或计费。</summary>
public sealed record GoogleVertexLlmOptions
{
    /// <summary>显式发送的 x-goog-user-project；不从主体或目标资源自动推断。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>完整 JSON、HTTP 错误或整次 SSE 的原始字节上限；超限失败，不截断。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首次候选终态/提示拦截之后的原生事件暂存上限，包含用量和扩展尾帧。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>一次原生流跟踪的候选索引数上限；不重编号或补造候选。</summary>
    public int MaximumTrackedCandidates { get; init; } = 128;
    /// <summary>Claude 正文版本；省略使用 vertex-2023-10-16，省略值不改变既有 Gemini 绑定身份。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaudeAnthropicVersion { get; init; }
}
