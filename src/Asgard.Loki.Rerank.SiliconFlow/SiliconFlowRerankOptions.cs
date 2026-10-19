using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank.SiliconFlow;

/// <summary>显式配置完整 SiliconFlow 重排目标，不复用其他能力的凭据或模型状态。</summary>
public sealed class SiliconFlowRerankOptions
{
    /// <summary>供应商实例的稳定身份。</summary>
    public string Id { get; init; } = "siliconflow-rerank";
    /// <summary>实际原生模型；模型支持的文本/图片及设置由调用者与上游约定。</summary>
    public required string Model { get; init; }
    /// <summary>运行时认证凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含原生版本和可选代理前缀的 HTTP(S) 根地址。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.siliconflow.cn/v1/");
    /// <summary>借用的 HTTP 工厂中已注册的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Rerank.SiliconFlow";
}
