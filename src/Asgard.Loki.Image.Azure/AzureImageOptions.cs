using System.Text.Json.Serialization;

namespace Asgard.Loki.Image.Azure;

/// <summary>完整 Azure 图像目标；资源、部署、版本及认证均由应用明确配置。</summary>
public sealed class AzureImageOptions
{
    /// <summary>完整供应商目标的稳定标识。</summary>
    public string Id { get; init; } = "azure-image";
    /// <summary>HTTP(S) 资源根地址，可包含应用前缀，不包含 openai/deployments。</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>原生部署路径段，不从公开模型名推导或删除点号。</summary>
    public required string Deployment { get; init; }
    /// <summary>原生 api-version，不选择默认日期或自动迁移接口。</summary>
    public required string ApiVersion { get; init; }
    /// <summary>固定目标的 API Key，不参与默认 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>可选原生正文 model；为空时省略，不等同于公开路由或部署名。</summary>
    public string? BodyModel { get; init; }
    /// <summary>应用拥有的工厂名称；应禁用重试、对冲及重定向。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Azure";
    /// <summary>JSON 正文或 multipart 文本/元数据的本地 UTF-8 字节上限，默认 4 MiB。</summary>
    public int MaxRequestBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>完整 JSON 响应或错误字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>每个借用图片/遮罩流本次剩余上传字节上限，默认 50 MiB；不验证像素或模型特有限制。</summary>
    public long MaxUploadBytes { get; init; } = 50L * 1024 * 1024;
}
