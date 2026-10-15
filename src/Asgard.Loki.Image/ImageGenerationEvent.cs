using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>统一保留图像生成进度、部分图像、最终图像及未知事件。</summary>
public sealed class ImageGenerationEvent
{
    /// <summary>保存协议事件和 SSE 元数据的独立快照。</summary>
    public ImageGenerationEvent(JsonElement data, ImageResultMetadata metadata, string? eventName = null,
        string? eventId = null, long? retryMilliseconds = null)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("图像事件必须是对象。", nameof(data));
        Data = data.Clone();
        Metadata = metadata;
        EventName = eventName;
        EventId = eventId;
        RetryMilliseconds = retryMilliseconds;
    }
    /// <summary>原始事件 JSON。</summary>
    public JsonElement Data { get; }
    /// <summary>实际调用的模型与供应商。</summary>
    public ImageResultMetadata Metadata { get; }
    /// <summary>协议事件类型。</summary>
    public string? Type => Data.TryGetProperty("type", out var value) ? value.GetString() : EventName;
    /// <summary>SSE 事件名。</summary>
    public string? EventName { get; }
    /// <summary>SSE 事件 ID。</summary>
    public string? EventId { get; }
    /// <summary>SSE 重连提示，不触发生成重试。</summary>
    public long? RetryMilliseconds { get; }
}
