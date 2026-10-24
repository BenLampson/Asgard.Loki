using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>只记录公共 SDK 格式校验后的原生请求，不模拟真实供应商格式效果。</summary>
internal sealed class OutputFormatProvider : IChatCompletionsAdapter, IResponsesAdapter, IClaudeMessagesAdapter, IGeminiAdapter, IOllamaChatAdapter, IOllamaGenerateAdapter, IBedrockConverseAdapter, ICohereChatAdapter, IWorkersAiTextAdapter
{
    /// <inheritdoc />
    public string Id => "formats";
    /// <summary>每次实际适配调用的完整快照。</summary>
    internal List<JsonObject> Calls { get; } = [];
    /// <summary>只提供公共结果包装需要的最小对象。</summary>
    private JsonElement Capture(JsonObject request)
    {
        Calls.Add(request.DeepClone().AsObject());
        return JsonSerializer.Deserialize<JsonElement>("""{"id":"format-result","status":"completed","output":[],"choices":[]}""");
    }
    /// <inheritdoc />
    Task<ChatCompletion> IChatCompletionsAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ChatCompletion(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<ChatCompletionEvent> IChatCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<Response> IResponsesAdapter.CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new Response(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<ClaudeMessage> IClaudeMessagesAdapter.CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ClaudeMessage(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<ClaudeMessageEvent> IClaudeMessagesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<GeminiContent> IGeminiAdapter.GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new GeminiContent(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<GeminiContentEvent> IGeminiAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<OllamaChatResponse> IOllamaChatAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new OllamaChatResponse(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<OllamaChatEvent> IOllamaChatAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<OllamaGenerateResponse> IOllamaGenerateAdapter.GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new OllamaGenerateResponse(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<OllamaGenerateEvent> IOllamaGenerateAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<BedrockConverseResponse> IBedrockConverseAdapter.ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new BedrockConverseResponse(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<CohereChatCompletion> ICohereChatAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new CohereChatCompletion(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<CohereChatEvent> ICohereChatAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request), context.Metadata);
    }
    /// <inheritdoc />
    Task<WorkersAiTextCompletion> IWorkersAiTextAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult(new WorkersAiTextCompletion(Capture(request), context.Metadata));
    /// <inheritdoc />
    async IAsyncEnumerable<WorkersAiTextEvent> IWorkersAiTextAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(Capture(request).GetRawText(), context.Metadata);
    }
}
