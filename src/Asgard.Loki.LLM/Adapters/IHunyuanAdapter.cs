namespace Asgard.Loki.LLM;

/// <summary>实现混元原生生成，保留 PascalCase JSON，认证和传输由供应商独立维护。</summary>
public interface IHunyuanAdapter : ILlmProvider
{
    /// <summary>仅调用固定目标，原生完整 JSON 不投影为其他协议。</summary>
    Task<HunyuanCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>保持原生记录顺序、工具关联、限制状态与 SSE 元数据，不聚合或补造终态。</summary>
    IAsyncEnumerable<HunyuanEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
