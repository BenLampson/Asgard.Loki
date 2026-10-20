using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>沿用完整目标绑定，Realtime 不借用 HTTP Responses 的响应归属记录。</summary>
internal sealed class RealtimeClient(LlmInvocation invocation) : IRealtimeClient
{
    /// <inheritdoc />
    public async Task<IRealtimeConnection> OpenAsync(string model, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        var call = await invocation.ResolveAsync(model, LlmProtocol.Realtime, options, null, cancellationToken).ConfigureAwait(false);
        var connection = await ((IRealtimeAdapter)call.Provider).OpenRealtimeAsync(call.Context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("原生 Realtime 适配器未返回连接。");
        try
        {
            await invocation.ValidateBoundAsync(call.Binding, null, cancellationToken).ConfigureAwait(false);
            if (connection.Metadata != call.Context.Metadata) throw new InvalidOperationException("连接返回了不同目标的元数据。");
            return new BoundRealtimeConnection(invocation, call.Binding, connection, call.Context.Metadata);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
