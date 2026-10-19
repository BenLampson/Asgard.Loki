using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS;

/// <summary>显式 Converse 操作，不选择 Claude raw InvokeModel 或修改模型专属正文。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>返回完整原生 Converse output/stopReason/usage/metrics 和未知字段。</summary>
    public async Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => ConverseStreamAsync(request, context, cancellationToken);
}
