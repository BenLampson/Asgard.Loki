using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Kling;

/// <summary>可灵原生视频完整上游配置，操作、实际模型和认证方式由应用明确选择。</summary>
public sealed class KlingVideoOptions
{
    /// <summary>完整目标稳定标识。</summary>
    public string Id { get; init; } = "kling-video";
    /// <summary>实际原生 model_name，不指定默认型号或服务端模型目录。</summary>
    public required string Model { get; init; }
    /// <summary>固定原生提交与查询操作。</summary>
    public required KlingVideoOperation Operation { get; init; }
    /// <summary>显式鉴权方式，不能同时配置两种凭据。</summary>
    public required KlingVideoAuthentication Authentication { get; init; }
    /// <summary>API Key 模式下的原始 Bearer，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>JWT 模式下的独立 Access Key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string AccessKeyId { get; init; } = "";
    /// <summary>JWT 模式下的独立 Secret Key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string SecretAccessKey { get; init; } = "";
    /// <summary>包含可选应用前缀的 HTTPS 根地址；地域由应用明确选择，不自动迁移。</summary>
    public Uri BaseAddress { get; init; } = new("https://api-singapore.klingai.com/");
    /// <summary>借用工厂中的客户端名；需关闭自动重试、对冲和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Kling";
    /// <summary>完整生成请求 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>完整原生结果或错误正文原始字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
