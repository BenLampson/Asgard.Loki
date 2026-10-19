namespace Asgard.Loki.LLM;

/// <summary>供应商实现的原生 Converse 协议，不复用 Claude 消息或业务历史协调。</summary>
public interface IBedrockConverseAdapter : ILlmProvider
{
    /// <summary>执行完整 Converse 调用，正文和结果保持原生结构。</summary>
    Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>读取独立的原生 ConverseStream 事件；是否实现由供应商矩阵明确说明。</summary>
    IAsyncEnumerable<BedrockConverseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
