using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.Google;

/// <summary>Gemini 原生 HTTP/JSON/SSE 的本地资源策略，不声明模型或参数能力。</summary>
public sealed record GoogleHttpOptions
{
    /// <summary>完整原生请求 UTF-8 字节上限，默认三十二 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>整个 JSON、HTTP 错误或 SSE 原始字节上限，默认三十二 MiB。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首个候选结束或全局错误起暂存的尾帧数量上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>同一次原生生成中不同候选的关联数量上限，不按索引大小分配。</summary>
    public int MaximumTrackedCandidates { get; init; } = 128;
    /// <summary>唯一显式认证选择；省略默认值不改变既有绑定字节。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GoogleHttpAuthentication Authentication { get; init; }
    /// <summary>可选原生操作与认证格式快照；省略保持原路径与绑定字节。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleHttpTransportOptions? Transport { get; init; }
}
