namespace Asgard.Loki.Internal;

/// <summary>供应商内部复用的标准 SSE 帧，不作为公共传输抽象发布。</summary>
internal sealed record SseFrame(string Data, string? EventName, string? EventId, long? RetryMilliseconds);
