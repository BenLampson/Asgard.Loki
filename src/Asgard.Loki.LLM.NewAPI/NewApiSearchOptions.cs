namespace Asgard.Loki.LLM.NewAPI;

/// <summary>显式固定 API Key 搜索的完整上游目标，不选择模型、账户或协议默认值。</summary>
public sealed record NewApiSearchOptions
{
    /// <summary>完整 HTTP(S) API 根，仅追加 alpha/search；不得包含凭据、查询或片段。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>调用者明确选择的实际搜索模型；该字段由目标拥有，不来自参数覆盖。</summary>
    public required string UpstreamModel { get; init; }
    /// <summary>应用定义的固定目标标识。</summary>
    public string TargetId { get; init; } = "newapi-search";
    /// <summary>调用者定义的配置版本，不代表服务端 API 稳定性。</summary>
    public string Version { get; init; } = "1";
    /// <summary>借用 HTTP 工厂的明确命名客户端。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.NewAPI.Search";
    /// <summary>完整原生请求 UTF-8 字节上限，默认一 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整成功或错误响应字节上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
}
