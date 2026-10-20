using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.Coze;

/// <summary>原生发起对话请求；显式用户和查询会话不被随机 ID 或 SDK 模型字段替代。</summary>
public sealed record CozeChatRequest
{
    /// <summary>调用者明确提供的原生 user_id，保留原始合法字符，不注入默认用户。</summary>
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }
    /// <summary>可选原生会话 ID，只进入查询，不写入正文；省略时由远端决定会话。</summary>
    [JsonIgnore]
    public string? ConversationId { get; init; }
    /// <summary>完整原生历史/工具/多模态引用、保存设置及未知参数；不执行工具或抓取引用资源。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
}
