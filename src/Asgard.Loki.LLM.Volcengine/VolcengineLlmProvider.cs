using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Volcengine.Internal;

namespace Asgard.Loki.LLM.Volcengine;

/// <summary>方舟原生 Chat/Responses HTTP/JSON 与 SSE，目标/模型/认证显式，不自动重试或转换协议。</summary>
public sealed class VolcengineLlmProvider : IChatCompletionsAdapter, IResponsesAdapter
{
    private readonly string _clientName;
    private readonly VolcengineHttpOptions _options;
    private readonly ArkHttpTransport _transport;
    /// <summary>保留既有构造签名，借用 HTTP 工厂；应用应关闭生成 POST 自动重试和重定向。</summary>
    public VolcengineLlmProvider(IHttpClientFactory httpClientFactory, string id = "volcengine", string httpClientName = "Asgard.Loki.LLM.Volcengine")
        : this(httpClientFactory, new VolcengineHttpOptions(), id, httpClientName) { }
    /// <summary>明确配置本地请求/正文/终态尾部与关联上限，策略参与完整 Session 绑定。</summary>
    public VolcengineLlmProvider(IHttpClientFactory httpClientFactory, VolcengineHttpOptions options, string id = "volcengine", string httpClientName = "Asgard.Loki.LLM.Volcengine")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArkJson.Text(id); ArkJson.Text(httpClientName); _options = ArkRequest.Options(options); _clientName = httpClientName; Id = id; _transport = new(httpClientFactory, httpClientName, _options);
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>严格 EOF/资源/关联语义与命名客户端/本地策略参与绑定；不复用旧提前结束的 Session。</summary>
    public string BindingIdentity => GetType().AssemblyQualifiedName + "\nark-http-v2\n" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName });
    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await _transport.PostAsync(Request("chat/completions", request, context, false, cancellationToken), cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await _transport.PostAsync(Request("responses", request, context, false, cancellationToken), cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _transport.StreamAsync(Request("chat/completions", request, context, true, cancellationToken), false, cancellationToken).ConfigureAwait(false))
        { if (frame.Data == "[DONE]") continue; yield return new(ArkJson.Parse(System.Text.Encoding.UTF8.GetBytes(frame.Data)), context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
    }
    /// <summary>原生 Responses 与 Chat 分别保持原有结果类型，不改写输出窗口。</summary>
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _transport.StreamAsync(Request("responses", request, context, true, cancellationToken), true, cancellationToken).ConfigureAwait(false))
        { if (frame.Data == "[DONE]") continue; yield return new(ArkJson.Parse(System.Text.Encoding.UTF8.GetBytes(frame.Data)), context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
    }
    /// <summary>首次工厂/传输等待前冻结完整原生请求；其他认证种类不借用空 API Key。</summary>
    private HttpRequestMessage Request(string path, JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        return ArkRequest.Create(context.Target.BaseAddress, path, context.Credential.ApiKey, payload, context.Target.UpstreamModel, stream, _options);
    }
}
