namespace Asgard.Loki.Infrastructure;

/// <summary>完整 SSE 事件及原始传输身份；不代表业务响应成功。</summary>
/// <param name="Data">按换行连接的原始 data 字段。</param>
/// <param name="EventName">当前事件名称，未指定时为 null。</param>
/// <param name="EventId">原始事件 ID，保留空值和跨事件继承。</param>
/// <param name="RetryMilliseconds">上游重连提示，仅作数据返回，不触发重试。</param>
public sealed record SseFrame(string Data, string? EventName, string? EventId, long? RetryMilliseconds);
