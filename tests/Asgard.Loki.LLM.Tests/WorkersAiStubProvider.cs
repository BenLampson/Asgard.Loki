using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>独立原生样本适配器，只验证核心编排，不模拟真实 Cloudflare HTTP。</summary>
internal sealed class WorkersAiStubProvider(string identity = "workers-text-test-v1") : IWorkersAiTextAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "workers-text-test";
    public string BindingIdentity => identity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal Action? OnResult { get; init; }
    internal Action? OnEnd { get; init; }
    internal Exception? Failure { get; init; }
    internal Exception? AfterFirstEvent { get; init; }
    internal bool Disposed { get; private set; }
    internal static JsonElement Part(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cloudflare", "native.json")));
        return document.RootElement.GetProperty(name).Clone();
    }
    Task<WorkersAiTextCompletion> IWorkersAiTextAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } failure) return Task.FromException<WorkersAiTextCompletion>(failure);
        OnResult?.Invoke(); return Task.FromResult(new WorkersAiTextCompletion(Part("response"), context.Metadata));
    }
    async IAsyncEnumerable<WorkersAiTextEvent> IWorkersAiTextAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue((request.DeepClone().AsObject(), context)); if (Failure is { } failure) throw failure;
        OnResult?.Invoke();
        foreach (var item in Part("events").EnumerateArray())
        {
            yield return new(item.GetProperty("data").GetString()!, context.Metadata, item.GetProperty("event_name").GetString(), item.GetProperty("event_id").GetString(), item.GetProperty("retry").GetInt64());
            if (AfterFirstEvent is { } error) throw error;
        }
        OnEnd?.Invoke(); await Task.CompletedTask;
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "other-protocol" }), context.Metadata));
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
}
