using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Vidu;

/// <summary>Vidu 原生视频的完整不可变目标配置，不注入模型或生成参数默认值。</summary>
public sealed class ViduVideoOptions
{
    /// <summary>完整目标稳定标识。</summary>
    public string Id { get; init; } = "vidu-video";
    /// <summary>实际原生 model，由应用按操作与账号支持范围提供。</summary>
    public required string Model { get; init; }
    /// <summary>固定的原生生成操作，查询仍保留该目标绑定。</summary>
    public required ViduVideoOperation Operation { get; init; }
    /// <summary>原生 Token 凭据，不参与默认配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>包含可选应用前缀的 HTTPS 根地址，不包含 ent/v2。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.vidu.com/");
    /// <summary>借用工厂中的客户端名，调用方需关闭重试、对冲与重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Vidu";
    /// <summary>原生 JSON 请求 UTF-8 字节上限，默认 20 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 20 * 1024 * 1024;
    /// <summary>原生结果或错误正文原始字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
