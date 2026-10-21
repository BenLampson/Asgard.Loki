using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>即梦 Visual 视频完整目标，不借用方舟推理 Key 或按输入媒体猜测服务标识。</summary>
public sealed class JimengVideoOptions
{
    /// <summary>完整上游目标的稳定标识。</summary>
    public string Id { get; init; } = "jimeng-visual-video";
    /// <summary>实际原生 req_key；明确选择所需视频接口，不提供默认型号或别名转换。</summary>
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
    /// <summary>借用工厂中的客户端名；须禁用自动重试与重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Volcengine.Jimeng";
    /// <summary>原生提交/查询正文 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>完整原生结果或错误正文的字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
