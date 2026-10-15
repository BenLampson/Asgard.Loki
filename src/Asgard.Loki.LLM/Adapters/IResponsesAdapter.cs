namespace Asgard.Loki.LLM;

/// <summary>将某个供应商的能力适配为 Responses 结果。</summary>
public interface IResponsesAdapter : ILlmProvider
{
    /// <summary>创建响应，原样保留响应 ID 与输出项。</summary>
    Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>创建并读取响应事件。</summary>
    IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
