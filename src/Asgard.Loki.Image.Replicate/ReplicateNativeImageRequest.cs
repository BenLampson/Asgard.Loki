using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Replicate;

/// <summary>调用者持有的固定模型目标上的完整原生预测请求，不执行公共路由。</summary>
public sealed class ReplicateNativeImageRequest
{
    /// <summary>仅标记结果元数据的公共模型名。</summary>
    public required string PublicModel { get; init; }
    /// <summary>完整原生根对象，包含 input 和可选 webhook 等字段；不插入模型或提示词。</summary>
    public JsonObject Parameters { get; init; } = [];
}
