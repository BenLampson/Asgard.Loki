namespace Asgard.Loki.LLM;

/// <summary>调用明确配置的 Workers AI 原生文本协议，不隐式选择模型格式或转换 Chat。</summary>
public interface IWorkersAiTextClient
{
    /// <summary>在完整 Session 目标上执行一次原生文本调用，保留完整原生 HTTP JSON 封装。</summary>
    Task<WorkersAiTextCompletion> CompleteAsync(WorkersAiTextRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐帧交付原生 JSON 与实际 DONE 控制数据，不聚合文本、工具或使用量。</summary>
    IAsyncEnumerable<WorkersAiTextEvent> StreamAsync(WorkersAiTextRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
