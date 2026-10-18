using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>仅实现 Responses 基础调用与显式 WebSocket 扩展，不伪装成其他协议供应商。</summary>
internal sealed class StubResponsesWebSocketProvider : IResponsesAdapter, IResponsesWebSocketAdapter
{
    private readonly StubLlmProvider _http = new();
    /// <inheritdoc />
    public string Id => "ws";
    /// <summary>模拟供应商配置版本变化，不能在旧连接上悄悄改变适配语义。</summary>
    internal string BindingVersion { get; set; } = "1";
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":" + BindingVersion;
    /// <summary>已打开的完整上下文和连接。</summary>
    internal ConcurrentQueue<(LlmAdapterContext Context, StubResponsesWebSocketConnection Connection)> Opened { get; } = new();
    /// <summary>模拟握手暂停、取消或失败。</summary>
    internal Func<LlmAdapterContext, CancellationToken, Task>? BeforeOpen { get; set; }
    /// <summary>模拟错误目标的元数据。</summary>
    internal LlmResultMetadata? OverrideMetadata { get; set; }
    /// <inheritdoc />
    public async Task<IResponsesWebSocketConnection> OpenWebSocketAsync(LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeOpen is not null) await BeforeOpen(context, cancellationToken);
        var connection = new StubResponsesWebSocketConnection(OverrideMetadata ?? context.Metadata);
        Opened.Enqueue((context, connection)); return connection;
    }
    /// <inheritdoc />
    public Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => _http.CreateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => ((IResponsesAdapter)_http).StreamAsync(request, context, cancellationToken);
}
