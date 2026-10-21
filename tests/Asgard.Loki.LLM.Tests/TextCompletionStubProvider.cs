using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>内存原生文本补全适配器，记录完整固定目标和请求，不调用真实供应商。</summary>
internal sealed class TextCompletionStubProvider(string bindingIdentity = "text-stub-v1") : ITextCompletionsAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "text-stub";
    public string BindingIdentity => bindingIdentity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal int ChatCalls { get; private set; }
    internal Exception? Failure { get; init; }
    internal Action? OnResult { get; init; }
    internal Exception? AfterFirstEvent { get; init; }
    internal bool Disposed { get; private set; }
    Task<TextCompletion> ITextCompletionsAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } error) return Task.FromException<TextCompletion>(error);
        using var document = JsonDocument.Parse("""{"id":"cmpl-fixture","object":"text_completion","model":"native-reported","choices":[{"index":0,"text":"text","finish_reason":"stop","logprobs":null}],"future":9007199254740993}""");
        OnResult?.Invoke(); return Task.FromResult(new TextCompletion(document.RootElement, context.Metadata));
    }
    async IAsyncEnumerable<TextCompletionEvent> ITextCompletionsAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ((ITextCompletionsAdapter)this).CompleteAsync(request, context, cancellationToken);
        yield return new(result.Data, context.Metadata, "native.text", "event-original", 750);
        if (AfterFirstEvent is { } error) throw error;
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ChatCalls++; return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "chat-fixture", choices = new object[0] }), context.Metadata)); }
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
}
