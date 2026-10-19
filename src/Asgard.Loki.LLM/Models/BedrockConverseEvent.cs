namespace Asgard.Loki.LLM;

/// <summary>ConverseStream 原生事件载荷与独立传输事件名，不构造通用消息模型。</summary>
public sealed class BedrockConverseEvent(JsonElement data, LlmResultMetadata metadata, string? eventName = null)
    : LlmJsonEvent(data, metadata, eventName, null, null)
{
    /// <summary>Converse 类型由原生传输事件名确定，载荷中的未知 type 字段不改变协议语义。</summary>
    public override string? Type => EventName;
}
