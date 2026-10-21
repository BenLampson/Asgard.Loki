using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Google;

/// <summary>Gemini 图片的完整固定目标，根地址及模型由调用者明确选择。</summary>
public sealed class GoogleImageOptions
{
    /// <summary>供应商目标身份。</summary>
    public string Id { get; init; } = "google-image";
    /// <summary>实际原生模型，无推测的默认模型。</summary>
    public required string Model { get; init; }
    /// <summary>包含 v1 或 v1beta 的原生版本根地址，不猜测版本。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>独立 Gemini x-goog-api-key 凭据。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>借用工厂中的客户端名称；应用应关闭自动重试和重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Google";
    /// <summary>原生请求 UTF-8 字节上限。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整 JSON 或整个 SSE 原始字节上限。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
