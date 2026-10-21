using System.Text.Json.Serialization;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>方舟私域媒体素材库的独立 AK/SK 配置，不借用视频生成 API Key。</summary>
public sealed class VolcengineAssetOptions
{
    /// <summary>Access Key 标识，不参与配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string AccessKeyId { get; init; } = "";
    /// <summary>Secret Key，不参与配置 JSON 序列化。</summary>
    [JsonIgnore]
    public string SecretAccessKey { get; init; } = "";
    /// <summary>可选临时凭据令牌，与 AK/SK 一起显式配置，不自动刷新。</summary>
    [JsonIgnore]
    public string? SecurityToken { get; init; }
    /// <summary>仅包含根路径的素材库服务地址，与推理 API 地址独立。</summary>
    public Uri BaseAddress { get; init; } = new("https://ark.cn-beijing.volcengineapi.com/");
    /// <summary>签名区域；调用者必须使其与所选服务地址一致。</summary>
    public string Region { get; init; } = "cn-beijing";
    /// <summary>借用 HTTP 工厂中的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Video.Volcengine.Assets";
    /// <summary>原生请求 UTF-8 字节上限，默认 1 MiB，不包含媒体二进制上传。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整结果或错误正文原始字节上限，默认 16 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
