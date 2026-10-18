using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Asgard.Loki.LLM.Tests;

/// <summary>内存双工替身；可暂停实际发送或按顺序注入原生事件，无网络与时间等待。</summary>
internal sealed class StubResponsesWebSocketConnection(LlmResultMetadata metadata) : IResponsesWebSocketConnection
{
    private readonly Channel<ResponseEvent> _events = Channel.CreateUnbounded<ResponseEvent>();
    /// <inheritdoc />
    public LlmResultMetadata Metadata { get; } = metadata;
    /// <summary>已实际发送的快照。</summary>
    internal ConcurrentQueue<JsonObject> Sent { get; } = new();
    /// <summary>暂停发送或模拟原生错误。</summary>
    internal Func<JsonObject, CancellationToken, ValueTask>? BeforeSend { get; set; }
    /// <summary>关闭次数。</summary>
    internal int CloseCount { get; private set; }
    /// <summary>释放次数。</summary>
    internal int DisposeCount { get; private set; }
    /// <summary>注入完整事件。</summary>
    internal void Emit(string json) => _events.Writer.TryWrite(new(JsonSerializer.Deserialize<JsonElement>(json), Metadata));
    /// <summary>结束本次读取。</summary>
    internal void Finish() => _events.Writer.TryComplete();
    /// <inheritdoc />
    public async ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeSend is not null) await BeforeSend(message, cancellationToken);
        Sent.Enqueue(message.DeepClone().AsObject());
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }
    /// <inheritdoc />
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); CloseCount++; Finish(); return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { DisposeCount++; Finish(); return ValueTask.CompletedTask; }
}
