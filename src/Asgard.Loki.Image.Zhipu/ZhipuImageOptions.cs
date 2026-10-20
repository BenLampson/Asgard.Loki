using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Zhipu;

/// <summary>独立 Zhipu 图片完整目标，不借用语音或视频凭据。</summary>
public sealed class ZhipuImageOptions
{
    /// <summary>图片目标身份。</summary>
    public string Id { get; init; } = "zhipu-image";
    /// <summary>实际原生图片模型，不提供默认目录或自动降级。</summary>
    public required string Model { get; init; }
    /// <summary>完整原生 v4 images/generations 地址，调用者明确选择地区和服务前缀。</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>独立原生 Bearer API Key。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>借用工厂客户端名称，应用应禁用自动重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Zhipu";
    /// <summary>完整原生请求 JSON 的 UTF-8 字节上限。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整原生结果/错误正文的字节上限。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
