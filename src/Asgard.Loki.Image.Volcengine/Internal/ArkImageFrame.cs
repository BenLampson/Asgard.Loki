using System.Text.Json;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>原生图片事件与独立 SSE 身份；延迟标记只约束交付，不写入原生 JSON。</summary>
internal sealed record ArkImageFrame(JsonElement Data, string RawData, string? EventName, string? EventId, long? Retry, bool Deferred);
