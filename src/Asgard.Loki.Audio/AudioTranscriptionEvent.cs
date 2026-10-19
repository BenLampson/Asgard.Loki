using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>完整原生转录事件及 SSE 身份；段落、说话人、增量、用量及未来字段不被归一化。</summary>
public sealed class AudioTranscriptionEvent
{
    /// <summary>复制原生 JSON 所有权；是否为完成事件由适配器依据自身协议确认。</summary>
    public AudioTranscriptionEvent(JsonElement data, bool isCompleted, AudioResultMetadata metadata,
        string? eventName = null, string? eventId = null, long? retryMilliseconds = null)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("转录事件必须为原生 JSON 对象。", nameof(data));
        ArgumentNullException.ThrowIfNull(metadata); Data = data.Clone(); IsCompleted = isCompleted; Metadata = metadata;
        EventName = eventName; EventId = eventId; RetryMilliseconds = retryMilliseconds;
    }
    /// <summary>完整原生 JSON；GetRawText 保留事件正文的原始 JSON 文本。</summary>
    public JsonElement Data { get; }
    /// <summary>适配器根据原生协议确认的本次转录完成状态。</summary>
    public bool IsCompleted { get; }
    /// <summary>本次事件所属的固定目标。</summary>
    public AudioResultMetadata Metadata { get; }
    /// <summary>原生 SSE event 名称。</summary>
    public string? EventName { get; }
    /// <summary>原生 SSE id，包括空字符串重置。</summary>
    public string? EventId { get; }
    /// <summary>原生重连提示；SDK 不据此重连或重发上传。</summary>
    public long? RetryMilliseconds { get; }
}
