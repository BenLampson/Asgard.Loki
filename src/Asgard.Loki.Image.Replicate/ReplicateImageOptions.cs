using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Replicate;

/// <summary>Replicate 原生模型图片预测的完整目标，不提供默认模型或输入参数推断。</summary>
public sealed class ReplicateImageOptions
{
    /// <summary>独立完整目标标识。</summary>
    public string Id { get; init; } = "replicate-images";
    /// <summary>实际 owner/name 模型资源，不支持版本号、部署或别名猜测。</summary>
    public required string Model { get; init; }
    /// <summary>独立 Bearer 凭据，不进入默认配置 JSON。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>原生 API 根地址，包括 v1 或调用者显式路径前缀。</summary>
    public Uri BaseAddress { get; init; } = new("https://api.replicate.com/v1/");
    /// <summary>借用 HTTP 工厂的客户端名，须禁用重试、重定向和对冲。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Replicate";
    /// <summary>单次预测请求 UTF-8 字节上限，默认 32 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>单次完整结果或错误的原始字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>单次模型输入上传的原始载荷字节上限，不含 multipart framing，默认 32 MiB。</summary>
    public long MaxUploadBytes { get; init; } = 32 * 1024 * 1024;
}
