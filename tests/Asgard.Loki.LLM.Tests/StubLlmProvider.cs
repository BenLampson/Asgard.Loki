using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>记录完整调用目标并返回确定性协议结果。</summary>
internal sealed class StubLlmProvider : IResponsesAdapter, IChatCompletionsAdapter
{
    private int _sequence;
    /// <inheritdoc />
    public string Id => "stub";
    /// <summary>每次调用的原始参数与已选择目标。</summary>
    internal ConcurrentQueue<(JsonObject Request, LlmAdapterContext Context)> Calls { get; } = new();
    /// <inheritdoc />
    public Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue((request.DeepClone().AsObject(), context));
        return Task.FromResult(new Response(JsonSerializer.SerializeToElement(new
        {
            id = "resp_" + Interlocked.Increment(ref _sequence), status = "completed", output = new object[0], extra = "保留"
        }), context.Metadata));
    }
    /// <inheritdoc />
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((request.DeepClone().AsObject(), context));
        return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "chat_1", choices = new object[0] }), context.Metadata));
    }
    /// <summary>输出包含响应 ID 和未知事件的完整 Responses 流。</summary>
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await CreateAsync(request, context, cancellationToken);
        yield return new ResponseEvent(JsonSerializer.SerializeToElement(new { type = "response.created", response = response.Data }), context.Metadata);
        yield return new ResponseEvent(JsonSerializer.SerializeToElement(new { type = "vendor.future", value = "不丢失" }), context.Metadata);
        yield return new ResponseEvent(JsonSerializer.SerializeToElement(new { type = "response.completed", response = response.Data }), context.Metadata);
    }
    /// <summary>输出一个确定性 Chat 分块。</summary>
    async IAsyncEnumerable<ChatCompletionEvent> IChatCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await CompleteAsync(request, context, cancellationToken);
        yield return new ChatCompletionEvent(response.Data, context.Metadata);
    }
}
