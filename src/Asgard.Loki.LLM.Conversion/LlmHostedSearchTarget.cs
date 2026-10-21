namespace Asgard.Loki.LLM.Conversion;

/// <summary>调用者对固定转换目标的原生搜索声明；不根据模型名猜测权限或版本。</summary>
public enum LlmHostedSearchTarget
{
    /// <summary>默认不启用跨协议托管搜索。</summary>
    None = 0,
    /// <summary>Responses 的正式 web_search 工具。</summary>
    ResponsesWebSearch = 1,
    /// <summary>Claude 的经典直接搜索工具，不启用动态代码过滤。</summary>
    ClaudeWebSearch20250305 = 2,
    /// <summary>Gemini generateContent 的 Google Search 文本搜索工具。</summary>
    GeminiGoogleSearch = 3,
    /// <summary>Chat 的原生 web_search_options；调用者确认固定上游具备先搜索再回答的语义。</summary>
    ChatWebSearch = 4
}
