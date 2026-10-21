using System.Text.Json.Serialization;

namespace Asgard.Loki.Audio.Azure;

/// <summary>显式固定 Azure 资源、部署、API 版本及 API Key，不发现模型或登录凭据。</summary>
public sealed class AzureAudioOptions
{
    /// <summary>调用者配置的完整目标实例标识。</summary>
    public string Id { get; init; } = "azure-audio";
    /// <summary>资源根地址；允许显式代理前缀，不包含 openai 操作路径或查询参数。</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>原生部署选择器，保留点号及原始大小写，不等同公共路由或底层模型名。</summary>
    public required string Deployment { get; init; }
    /// <summary>调用者选择的原生版本，不使用默认版本或自动升级。</summary>
    public required string ApiVersion { get; init; }
    /// <summary>仅发送为 api-key；不会借用其他能力的令牌。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>可选的固定原生请求体 model；为空时不补造此字段或 part。</summary>
    public string? BodyModel { get; init; }
    /// <summary>借用 HTTP 工厂的命名客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Audio.Azure";
    /// <summary>语音 JSON 及非音频上传参数的 UTF-8 字节上限。</summary>
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON/文本结果及错误正文的字节上限。</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>单次合成的音频字节上限，达到上限后不重试。</summary>
    public long MaxAudioBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>当前位置之后的原始上传字节上限。</summary>
    public long MaxUploadBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>语音本地读取缓冲区大小，不代表编码帧或总长度。</summary>
    public int ReadBufferSize { get; init; } = 8192;
}
