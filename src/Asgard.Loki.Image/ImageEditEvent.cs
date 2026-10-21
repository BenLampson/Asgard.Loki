using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>保留原生图像编辑事件、目标和 SSE 身份；适配器按自身协议确认成功终态。</summary>
public sealed class ImageEditEvent
{
    /// <summary>复制原生 JSON 所有权，不从事件名称或 EOF 猜测完成。</summary>
    public ImageEditEvent(JsonElement data, bool isCompleted, ImageResultMetadata metadata,
        string? eventName = null, string? eventId = null, long? retryMilliseconds = null)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("编辑事件必须为原生 JSON 对象。", nameof(data));
        ArgumentNullException.ThrowIfNull(metadata); Data = data.Clone(); IsCompleted = isCompleted; Metadata = metadata;
        EventName = eventName; EventId = eventId; RetryMilliseconds = retryMilliseconds;
    }
    /// <summary>完整原生事件，含部分/最终图像、用量及未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>供应商按原生协议确认的本次编辑成功完成标志。</summary>
    public bool IsCompleted { get; }
    /// <summary>本次执行的完整目标身份。</summary>
    public ImageResultMetadata Metadata { get; }
    /// <summary>原生 SSE 事件名称。</summary>
    public string? EventName { get; }
    /// <summary>原生 SSE 事件 ID。</summary>
    public string? EventId { get; }
    /// <summary>原生重连提示，不引起自动重连或重新上传。</summary>
    public long? RetryMilliseconds { get; }
}
