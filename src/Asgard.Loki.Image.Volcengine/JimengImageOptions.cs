using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>即梦 Visual 同步图片调用的完整目标，独立于方舟 Bearer 目标。</summary>
public sealed class JimengImageOptions
{
    /// <summary>完整目标的稳定标识。</summary>
    public string Id { get; init; } = "jimeng-visual-image";
    /// <summary>实际原生 req_key，不提供默认型号、别名或迁移。</summary>
    public required string RequestKey { get; init; }
    /// <summary>独立 Access Key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string AccessKeyId { get; init; } = "";
    /// <summary>独立 Secret Key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string SecretAccessKey { get; init; } = "";
    /// <summary>可选临时凭据令牌，参与签名，不自动发现或刷新。</summary>
    [JsonIgnore]
    public string? SecurityToken { get; init; }
    /// <summary>Visual HTTPS 根地址，不包含路径前缀、查询或片段。</summary>
    public Uri BaseAddress { get; init; } = new("https://visual.volcengineapi.com/");
    /// <summary>借用工厂中的客户端名；应用须禁用自动重试与重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Volcengine.Jimeng";
    /// <summary>完整请求的 UTF-8 字节上限，默认 32 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整原生结果或错误正文的字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
