using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>通过显式 ClaudeMessages 协议访问 rawPredict，不根据模型名称自动转换。</summary>
public sealed partial class GoogleVertexLlmProvider
{
    /// <summary>返回未经 Gemini/Chat 映射的原生 Claude 完整结果。</summary>
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <summary>Claude SSE 属于下一独立检查点，不能用 Gemini 事件或完整 JSON 伪装。</summary>
    IAsyncEnumerable<ClaudeMessageEvent> IClaudeMessagesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("此检查点尚未实现 Vertex Claude SSE。");
}
