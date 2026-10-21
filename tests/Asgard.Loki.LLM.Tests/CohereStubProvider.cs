using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>只转交独立原生样本的内存适配器，不模拟网络或供应商成功。</summary>
internal sealed class CohereStubProvider(string identity = "cohere-test-v1") : ICohereChatAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "cohere-test";
    public string BindingIdentity => identity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal Action? OnResult { get; init; }
    internal Exception? Failure { get; init; }
    internal Exception? AfterFirstEvent { get; init; }
    internal bool Disposed { get; private set; }
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cohere", name));
    Task<CohereChatCompletion> ICohereChatAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } failure) return Task.FromException<CohereChatCompletion>(failure);
        using var document = JsonDocument.Parse(Fixture("response.json")); OnResult?.Invoke();
        return Task.FromResult(new CohereChatCompletion(document.RootElement, context.Metadata));
    }
    async IAsyncEnumerable<CohereChatEvent> ICohereChatAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } failure) throw failure;
        using var document = JsonDocument.Parse(Fixture("events.json")); OnResult?.Invoke();
        foreach (var data in document.RootElement.EnumerateArray())
        { yield return new(data, context.Metadata); if (AfterFirstEvent is { } error) throw error; }
        await Task.CompletedTask;
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "other-protocol" }), context.Metadata));
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
}
