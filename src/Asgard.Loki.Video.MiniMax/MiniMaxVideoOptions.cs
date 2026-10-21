using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.MiniMax;

/// <summary>MiniMax 视频完整目标，版本、实际模型与凭据均由调用者明确绑定。</summary>
public sealed class MiniMaxVideoOptions
{
    /// <summary>完整上游的稳定标识。</summary>
    public string Id { get; init; } = "minimax-video";
    /// <summary>实际原生模型，不提供默认模型或模型目录。</summary>
    public required string Model { get; init; }
    /// <summary>显式原生协议版本，不从模型名切换。</summary>
    public required MiniMaxVideoApiVersion ApiVersion { get; init; }
    /// <summary>原始 API Key，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>不含 v1/v2 的服务根地址；区域或代理前缀由调用者明确指定。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.minimax.io/");
    /// <summary>借用工厂中的客户端名；须关闭自动重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.MiniMax";
    /// <summary>提交正文 UTF-8 字节上限，默认 64 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>完整原生结果或错误字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
