using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>四种文本完整调用复用同一固定 Converse 上游，不构造额外调度层或原生传输。</summary>
public sealed partial class ConvertingBedrockLlmProvider
{
    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await Complete(request, LlmProtocol.ChatCompletions, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await Complete(request, LlmProtocol.Responses, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <summary>Claude 与 Responses 的同名入口按各自协议分派。</summary>
    async Task<ClaudeMessage> IClaudeMessagesAdapter.CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        var result = await Complete(request, LlmProtocol.ClaudeMessages, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <inheritdoc />
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var result = await Complete(request, LlmProtocol.Gemini, context, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Metadata);
    }
    /// <summary>混合 Chat 流未实现，不获取原生凭据、创建 HTTP 或降级为完整调用。</summary>
    public IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => UnsupportedTextStream<ChatCompletionEvent>(cancellationToken);
    /// <summary>完整 Responses 转换不代表其事件状态机已经具备 Converse 语义。</summary>
    IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => UnsupportedTextStream<ResponseEvent>(cancellationToken);
    /// <summary>完整 Claude 转换不生成虚构的原生事件尾部。</summary>
    IAsyncEnumerable<ClaudeMessageEvent> IClaudeMessagesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => UnsupportedTextStream<ClaudeMessageEvent>(cancellationToken);
    /// <summary>Gemini 混合流独立于完整请求支持矩阵。</summary>
    IAsyncEnumerable<GeminiContentEvent> IGeminiAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => UnsupportedTextStream<GeminiContentEvent>(cancellationToken);
    /// <summary>取消仍优先传播；未实现的流不枚举任何原生源。</summary>
    private static IAsyncEnumerable<T> UnsupportedTextStream<T>(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); throw new NotSupportedException("四种文本协议与 Converse 的混合流尚未实现。");
    }
}
