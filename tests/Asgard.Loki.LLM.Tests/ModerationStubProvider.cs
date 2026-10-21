using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>只在内存记录原生调用及完整目标，不使用 HTTP 或真实分类模型。</summary>
internal sealed class ModerationStubProvider(string bindingIdentity = "moderation-stub-v1") : IModerationsAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "moderation-stub";
    public string BindingIdentity => bindingIdentity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal int ChatCalls { get; private set; }
    internal Exception? Failure { get; init; }
    internal bool Disposed { get; private set; }
    public Task<Moderation> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } error) return Task.FromException<Moderation>(error);
        using var document = JsonDocument.Parse("""{"id":"modr-fixture","model":"native-reported","results":[{"flagged":false,"categories":{},"category_scores":{}}],"future":9007199254740993}""");
        return Task.FromResult(new Moderation(document.RootElement, context.Metadata));
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ChatCalls++; return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "chat-fixture", choices = new object[0] }), context.Metadata)); }
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
}
