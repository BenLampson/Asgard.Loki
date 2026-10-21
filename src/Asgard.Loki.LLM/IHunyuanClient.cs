namespace Asgard.Loki.LLM;

/// <summary>在固定完整 Session 目标调用混元原生协议，不隐式转换为 OpenAI Chat。</summary>
public interface IHunyuanClient
{
    /// <summary>执行一次原生完整 JSON 生成，保留 Response 信封和所有扩展。</summary>
    Task<HunyuanCompletion> CompleteAsync(HunyuanRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>转交原生 SSE 记录；供应商负责原生终态、错误与完整资源关闭验证。</summary>
    IAsyncEnumerable<HunyuanEvent> StreamAsync(HunyuanRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
