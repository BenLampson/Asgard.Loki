namespace Asgard.Loki.LLM;

/// <summary>对外提供统一的 Responses 创建与续接。</summary>
public interface IResponsesClient
{
    /// <summary>创建完整响应。</summary>
    Task<Response> CreateAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>创建并读取完整协议事件。</summary>
    IAsyncEnumerable<ResponseEvent> StreamAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
