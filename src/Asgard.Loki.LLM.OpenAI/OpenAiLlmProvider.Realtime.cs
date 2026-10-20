using System.Collections.ObjectModel;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>独立原生 Realtime 连接，复用的只有无业务语义的 JSON 分帧。</summary>
public sealed partial class OpenAiLlmProvider
{
    /// <summary>显式配置 Realtime 资源限制及借用工厂，保留现有所有构造签名。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, OpenAiRealtimeOptions realtimeOptions,
        IOpenAiRealtimeWebSocketFactory? realtimeFactory = null, string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, null, null, null, realtimeOptions, realtimeFactory)
        => ArgumentNullException.ThrowIfNull(realtimeOptions);

    /// <summary>同时显式配置三种传输的独立策略，不共享业务状态或重试流程。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, OpenAiHttpOptions httpOptions,
        OpenAiResponsesWebSocketOptions webSocketOptions, OpenAiRealtimeOptions realtimeOptions,
        IOpenAiResponsesWebSocketFactory? webSocketFactory = null, IOpenAiRealtimeWebSocketFactory? realtimeFactory = null,
        string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, webSocketFactory, webSocketOptions, httpOptions, realtimeOptions, realtimeFactory)
    {
        ArgumentNullException.ThrowIfNull(httpOptions); ArgumentNullException.ThrowIfNull(webSocketOptions);
        ArgumentNullException.ThrowIfNull(realtimeOptions);
    }

    /// <inheritdoc />
    public async Task<IRealtimeConnection> OpenRealtimeAsync(LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Realtime 目标必须是无凭据、查询和片段的 HTTP(S) 基础地址。", nameof(context));
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); OpenAiHttpJson.Text(context.Target.UpstreamModel);
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/realtime");
        var address = new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Port = endpoint.IsDefaultPort ? -1 : endpoint.Port,
            Query = "model=" + Uri.EscapeDataString(context.Target.UpstreamModel)
        }.Uri;
        var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Authorization"] = "Bearer " + context.Credential.ApiKey });
        var socket = await _realtimeFactory.ConnectAsync(address, headers, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Realtime 工厂未返回 WebSocket。");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new OpenAiRealtimeConnection(new(socket, _realtimeOptions.MaximumMessageBytes), context.Metadata);
        }
        catch { socket.Dispose(); throw; }
    }
}
