using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>离线星火适配器，只转交独立原生消息窗口，不建立真实连接或解释供应商终态。</summary>
internal sealed class SparkStubProvider(string bindingIdentity = "spark-stub-v1") : ISparkAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "spark-stub";
    public string BindingIdentity => bindingIdentity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal int ChatCalls { get; private set; }
    internal Exception? Failure { get; init; }
    internal Exception? AfterFirstEvent { get; init; }
    internal Action? OnResult { get; init; }
    internal Action? OnStreamClosed { get; init; }
    internal bool StreamClosed { get; private set; }
    internal bool Disposed { get; private set; }
    Task<SparkCompletion> ISparkAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } error) return Task.FromException<SparkCompletion>(error); OnResult?.Invoke();
        return Task.FromResult(new SparkCompletion(Part("frames").EnumerateArray().Select(data => new SparkEvent(data, context.Metadata)), context.Metadata));
    }
    async IAsyncEnumerable<SparkEvent> ISparkAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ((ISparkAdapter)this).CompleteAsync(request, context, cancellationToken);
        try { foreach (var item in result.Events) { yield return item; if (AfterFirstEvent is { } error) throw error; } }
        finally { StreamClosed = true; OnStreamClosed?.Invoke(); }
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ChatCalls++; return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "chat-fixture", choices = new object[0] }), context.Metadata)); }
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spark.json"))); return document.RootElement.GetProperty(name).Clone(); }
}
