namespace Asgard.Loki.LLM;

/// <summary>供应商实现的 Nova Invoke 原生契约，不按模型名选择或转换协议。</summary>
public interface IBedrockNovaAdapter : ILlmProvider
{
    /// <summary>执行完整原生调用，不提取首个文本块或重算用量。</summary>
    Task<BedrockNovaResponse> InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>显式原生流入口；供应商是否实现由自己的支持矩阵说明。</summary>
    IAsyncEnumerable<BedrockNovaEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
