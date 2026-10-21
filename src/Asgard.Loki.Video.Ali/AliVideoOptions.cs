using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Ali;

/// <summary>独立万相视频完整目标配置，原生操作、实际模型、区域根地址与凭据由调用者明确绑定。</summary>
public sealed class AliVideoOptions
{
    /// <summary>完整上游目标的稳定标识。</summary>
    public string Id { get; init; } = "ali-video";
    /// <summary>实际原生模型名称，不提供 SDK 猜测的默认模型。</summary>
    public required string Model { get; init; }
    /// <summary>明确的原生提交服务，不按模型名称切换。</summary>
    public required AliVideoOperation Operation { get; init; }
    /// <summary>独立百炼 API Key，不参与配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含 api/v1 路径的服务根地址；业务空间及区域地址需由调用者明确选择。</summary>
    public Uri BaseAddress { get; init; } = new("https://dashscope.aliyuncs.com/api/v1/");
    /// <summary>借用 HTTP 工厂中的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Ali";
    /// <summary>原生请求 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>原生结果或错误正文的字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
