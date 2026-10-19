using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS;

/// <summary>显式 Converse 操作，不选择 Claude raw InvokeModel 或修改模型专属正文。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>返回完整原生 Converse output/stopReason/usage/metrics 和未知字段。</summary>
    public async Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <summary>ConverseStream 与 Claude EventStream 不是相同的内层协议，后续独立实现。</summary>
    public IAsyncEnumerable<BedrockConverseEvent> ConverseStreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("Bedrock ConverseStream 尚未实现。");
    }
    /// <inheritdoc />
    IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => ConverseStreamAsync(request, context, cancellationToken);
}
