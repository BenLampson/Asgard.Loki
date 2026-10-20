using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>供应商直接原生调用，允许图层拆分省略 prompt，不改造统一便利请求的文本要求。</summary>
public sealed record VolcengineNativeImageRequest
{
    /// <summary>调用者的公共模型标签，仅用于结果元数据；直接调用不执行模块路由选择。</summary>
    public required string PublicModel { get; init; }
    /// <summary>完整原生 JSON 参数，实际 model 由固定目标配置绑定；不包含中转站文件占位符。</summary>
    public required JsonObject Parameters { get; init; }
}
