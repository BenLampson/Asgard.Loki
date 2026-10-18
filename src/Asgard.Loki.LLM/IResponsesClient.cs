namespace Asgard.Loki.LLM;

/// <summary>对外提供统一的 Responses 创建与续接。</summary>
public interface IResponsesClient
{
    /// <summary>创建完整响应。</summary>
    Task<Response> CreateAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>显式压缩原生上下文窗口；旧自定义客户端默认不支持该可选操作。</summary>
    Task<ResponseCompaction> CompactAsync(ResponseCompactionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
        => Task.FromException<ResponseCompaction>(new NotSupportedException("此 Responses 客户端不支持原生压缩操作。"));
    /// <summary>建立原生双工连接的可选扩展；旧客户端默认报告不支持。</summary>
    Task<IResponsesWebSocketConnection> OpenWebSocketAsync(string model, LlmCallOptions options, CancellationToken cancellationToken = default)
        => Task.FromException<IResponsesWebSocketConnection>(new NotSupportedException("此 Responses 客户端不支持原生 WebSocket 连接。"));
    /// <summary>创建并读取完整协议事件。</summary>
    IAsyncEnumerable<ResponseEvent> StreamAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
