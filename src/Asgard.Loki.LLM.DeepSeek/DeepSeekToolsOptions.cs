using System.Text.Json.Serialization;

namespace Asgard.Loki.LLM.DeepSeek;

/// <summary>DeepSeek 原生搜索/页面获取的固定服务配置，不创建模型路由、会话或工具执行层。</summary>
public sealed class DeepSeekToolsOptions
{
    /// <summary>原生 tools API 根；可明确配置代理前缀，只追加 search/fetch，不推断模型或地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.deepseek.com/v1/tools/");
    /// <summary>独立明确提供的 API Key，不从模型客户端、环境或其他能力借用，不进入配置 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>借用的命名 HTTP 客户端，工厂和处理器始终由应用拥有。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.DeepSeek.Tools";
    /// <summary>单次原生 JSON 请求的 UTF-8 字节上限，默认 1 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>单次完整结果或错误的原始字节上限，默认 16 MiB，不截断或隐式续取。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
