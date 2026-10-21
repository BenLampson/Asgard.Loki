using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>离线混元适配器，独立样本与真实内存会话配合验证完整目标，不调用供应商。</summary>
internal sealed class HunyuanStubProvider(string bindingIdentity = "hunyuan-stub-v1") : IHunyuanAdapter, IChatCompletionsAdapter, IDisposable
{
    public string Id => "hunyuan-stub";
    public string BindingIdentity => bindingIdentity;
    internal ConcurrentQueue<(JsonObject Body, LlmAdapterContext Context)> Calls { get; } = new();
    internal int ChatCalls { get; private set; }
    internal Exception? Failure { get; init; }
    internal Exception? AfterFirstEvent { get; init; }
    internal Action? OnResult { get; init; }
    internal Action? OnStreamClosed { get; init; }
    internal bool StreamClosed { get; private set; }
    internal bool Disposed { get; private set; }
    Task<HunyuanCompletion> IHunyuanAdapter.CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        if (Failure is { } failure) return Task.FromException<HunyuanCompletion>(failure);
        OnResult?.Invoke(); return Task.FromResult(new HunyuanCompletion(Fixture("response"), context.Metadata));
    }
    async IAsyncEnumerable<HunyuanEvent> IHunyuanAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await ((IHunyuanAdapter)this).CompleteAsync(request, context, cancellationToken);
        try
        {
            foreach (var item in Fixture("events").EnumerateArray())
            {
                yield return new(item, context.Metadata, "native.delta", "native-id", 600);
                if (AfterFirstEvent is { } failure) throw failure;
            }
        }
        finally { StreamClosed = true; OnStreamClosed?.Invoke(); }
    }
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ChatCalls++; return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new { id = "chat-fixture", choices = new object[0] }), context.Metadata)); }
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { var result = await CompleteAsync(request, context, cancellationToken); yield return new(result.Data, result.Metadata); }
    public void Dispose() => Disposed = true;
    /// <summary>样本先于生产实现独立编写；复制文档所有权后立即释放解析器。</summary>
    internal static JsonElement Fixture(string name)
    { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hunyuan.json"))); return doc.RootElement.GetProperty(name).Clone(); }
}
