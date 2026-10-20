using System.Text.Json;

namespace Asgard.Loki.LLM.Volcengine;

/// <summary>完整原生应用 JSON 结果，不选择最后答案、聚合模型/插件用量或改写返回模型。</summary>
public sealed class VolcengineBotResult
{
    /// <summary>传输、资源关闭和最终检查完成后创建独立 JSON 所有权。</summary>
    internal VolcengineBotResult(JsonElement data, VolcengineBotMetadata metadata) { Data = data.Clone(); Metadata = metadata; }
    /// <summary>全部原生 choices、model、bot_usage、references、metadata 和未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>固定 Bot 服务元数据，底层模型仍以原生 Data 为准。</summary>
    public VolcengineBotMetadata Metadata { get; }
}
