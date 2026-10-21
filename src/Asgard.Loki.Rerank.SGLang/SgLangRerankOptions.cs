using System.Text.Json.Serialization;

namespace Asgard.Loki.Rerank.SGLang;

/// <summary>配置完整原生 SGLang 重排目标，不启动模型服务器或管理模型。</summary>
public sealed class SgLangRerankOptions
{
    /// <summary>独立且稳定的供应商目标身份。</summary>
    public string Id { get; init; } = "sglang-rerank";
    /// <summary>调用者声明的已部署模型身份，仅记录在元数据中，不向原生请求注入 model。</summary>
    public required string Model { get; init; }
    /// <summary>调用者提供的完整 API 根路径，例如包含 /v1/ 的 HTTP(S) 地址。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>显式选择匿名或固定 Bearer 认证。</summary>
    public required SgLangRerankAuthentication Authentication { get; init; }
    /// <summary>仅 Bearer 模式使用的凭据，不参与 JSON 序列化。</summary>
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
    /// <summary>借用工厂中独立注册的客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Rerank.SGLang";
    /// <summary>完整请求 UTF-8 字节上限，默认 32 MiB，不按字符计数或拆批。</summary>
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>原始成功或错误响应字节上限，默认 64 MiB。</summary>
    public int MaxResponseBytes { get; init; } = 64 * 1024 * 1024;
}
