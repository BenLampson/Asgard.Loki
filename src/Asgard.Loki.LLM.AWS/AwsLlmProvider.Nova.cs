using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Nova 通过显式协议选择 Invoke；只移除 SDK 路由模型，其他原生正文不改写。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>使用原生 output/message、工具与用量，不套用 Claude 版本策略。</summary>
    public async Task<BedrockNovaResponse> InvokeNovaAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, false, cancellationToken, nova: true).ConfigureAwait(false), context.Metadata);
    /// <summary>通过独立原生契约调用，不靠模型名称切换行为。</summary>
    Task<BedrockNovaResponse> IBedrockNovaAdapter.InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => InvokeNovaAsync(request, context, cancellationToken);
    /// <summary>通过独立 Nova 解码路径读取 InvokeModelWithResponseStream，不套用 Claude 事件。</summary>
    IAsyncEnumerable<BedrockNovaEvent> IBedrockNovaAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => NovaStreamAsync(request, context, cancellationToken);
}
