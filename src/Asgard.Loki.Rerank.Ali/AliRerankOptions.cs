using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank.Ali;

/// <summary>配置独立完整的百炼 DashScope 原生重排目标，不推断工作空间或地域。</summary>
public sealed class AliRerankOptions
{
    /// <summary>供应商实例的稳定标识。</summary>
    public string Id { get; init; } = "ali-rerank";
    /// <summary>实际模型名，媒体和参数支持由调用者与上游约定。</summary>
    public required string Model { get; init; }
    /// <summary>包含 api/v1/ 的固定 HTTP(S) 根地址，必须由调用者明确选择。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>调用者提供的百炼 API Key，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>外部 HTTP 工厂中注册的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Rerank.Ali";
}
