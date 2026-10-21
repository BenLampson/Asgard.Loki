using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>Realtime 仅转交原生 JSON，不借用 Responses 生命周期、请求队列或响应归属。</summary>
internal sealed class AzureRealtimeConnection(AzureRealtimeTransport transport, LlmResultMetadata metadata) : IRealtimeConnection
{
    private int _reader;
    /// <inheritdoc />
    public LlmResultMetadata Metadata { get; } = metadata;
    /// <inheritdoc />
    public ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message); cancellationToken.ThrowIfCancellationRequested();
        AzureJson.Input(message);
        var snapshot = message.DeepClone().AsObject();
        var type = RequiredType(snapshot);
        // 只限制已知的模型选择位置，不遍历工具参数中的同名业务字段。
        if (type == "session.update" && snapshot["session"] is JsonObject session) CheckModel(session);
        if (type == "response.create" && snapshot["response"] is JsonObject response) CheckModel(response);
        return transport.SendAsync(snapshot, cancellationToken);
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("同一 Realtime 连接只允许一个并发读取者。");
        try
        {
            while (await transport.ReadAsync(cancellationToken).ConfigureAwait(false) is { } data)
            {
                if (!data.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(type.GetString()))
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidDataException("原生 Realtime 事件缺少有效 type。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                // error、response.done 的所有状态和未知事件均保持原样；不合成终态或重试。
                yield return new RealtimeEvent(data, Metadata);
            }
        }
        finally { Volatile.Write(ref _reader, 0); }
    }
    /// <inheritdoc />
    public ValueTask CloseAsync(CancellationToken cancellationToken = default) => transport.CloseAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => transport.DisposeAsync();
    /// <summary>同一连接不能改变已选模型；接受公共名称或原实际名称，仅规范已知模型位置。</summary>
    private void CheckModel(JsonObject value)
    {
        if (!value.ContainsKey("model")) return;
        if (value["model"] is not JsonValue model || !model.TryGetValue<string>(out var text)
            || text != Metadata.PublicModel && text != Metadata.UpstreamModel)
            throw new ArgumentException("Realtime 连接不能改变已绑定模型。", nameof(value));
        value["model"] = Metadata.UpstreamModel;
    }
    /// <summary>允许未来事件类型，但不能把缺失类型的对象当成协议事件。</summary>
    private static string RequiredType(JsonObject value)
        => value["type"] is JsonValue type && type.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new ArgumentException("原生 Realtime 事件必须具有非空文本 type。", nameof(value));
}
