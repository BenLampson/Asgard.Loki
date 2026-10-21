using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>通过显式 ClaudeMessages 协议访问 rawPredict，不根据模型名称自动转换。</summary>
public sealed partial class GoogleVertexLlmProvider
{
    /// <summary>返回未经 Gemini/Chat 映射的原生 Claude 完整结果。</summary>
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
}
