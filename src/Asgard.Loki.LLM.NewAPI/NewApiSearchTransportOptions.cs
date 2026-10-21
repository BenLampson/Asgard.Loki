namespace Asgard.Loki.LLM.NewAPI;

/// <summary>单个原生搜索操作的相对地址与非密钥凭据格式，不定义路由或搜索工作流。</summary>
public sealed record NewApiSearchTransportOptions
{
    /// <summary>追加到完整 API 根的相对搜索路径，可含固定查询；不接受其他服务器、模板或片段。</summary>
    public string SearchPath { get; init; } = "alpha/search";
    /// <summary>Header/Query 必须明确的头或参数名；其他方式必须省略。</summary>
    public string? CredentialName { get; init; }
    /// <summary>Header/Query 中选中密钥前的固定非密钥文本。</summary>
    public string CredentialPrefix { get; init; } = "";
    /// <summary>Header/Query 中选中密钥后的固定非密钥文本。</summary>
    public string CredentialSuffix { get; init; } = "";
}
