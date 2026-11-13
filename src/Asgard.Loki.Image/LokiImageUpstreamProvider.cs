using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Image;

/// <summary>地址、实际模型、凭据和 Handler 的完整上游目标。</summary>
public sealed record LokiImageUpstreamProvider
{
    /// <summary>公开模型内唯一的完整目标身份。</summary>
    public required string Id { get; init; }
    /// <summary>由具体 Handler 解释的版本根或完整原生端点，允许显式中转站。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>实际上游模型。</summary>
    public required string UpstreamModel { get; init; }
    /// <summary>上游凭据，不参与默认序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>可复用的协议处理器对象，与 HandlerName 二选一。</summary>
    [JsonIgnore]
    public ILokiImageGenerationHandler? Handler { get; init; }
    /// <summary>注册的协议处理器名称，与 Handler 二选一。</summary>
    public string? HandlerName { get; init; }
    /// <summary>应用拥有的命名 HTTP 客户端，应禁用生成重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Generation";
    /// <summary>本实际模型支持的统一可选参数；缺项明确拒绝，原生调用不受此表限制。</summary>
    public IReadOnlyDictionary<string, LokiImageParameterRule> Parameters { get; init; }
        = new Dictionary<string, LokiImageParameterRule>();
    /// <summary>Handler 专属目标设置，例如组织、部署和 API 版本，不作为生成参数发送。</summary>
    public JsonObject Settings { get; init; } = new();
    /// <summary>仅显示目标身份，不把凭据或专属设置写入诊断字符串。</summary>
    public override string ToString() => $"{Id} ({UpstreamModel}, {Handler?.Name ?? HandlerName})";
}
