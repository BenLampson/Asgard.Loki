namespace Asgard.Loki.LLM;

/// <summary>单个完整上游目标的原生 Responses 双工连接；不执行工具、重连或重放。</summary>
public interface IResponsesWebSocketConnection : IAsyncDisposable
{
    /// <summary>连接绑定的公共模型及实际上游身份，不包含凭据。</summary>
    LlmResultMetadata Metadata { get; }
    /// <summary>发送原生 JSON 事件；实现必须复制调用者数据并保留未知字段。</summary>
    ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default);
    /// <summary>读取完整原生事件，包括未知事件、流标识和失败终态；只允许一个并发读取者。</summary>
    IAsyncEnumerable<ResponseEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
    /// <summary>由调用者显式关闭连接；缓存不能恢复该连接的 TCP 或服务器状态。</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
