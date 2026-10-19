using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Express 原生 Gemini 短资源/API Key 调用；同包独立实现，不借用云端身份或管理服务。</summary>
public sealed class GoogleVertexExpressLlmProvider : IGeminiAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly GoogleVertexExpressLlmOptions _options;
    /// <summary>借用 HTTP 工厂；根地址显式包含版本，模型显式提供完整 publishers/google/models 短资源。</summary>
    public GoogleVertexExpressLlmProvider(IHttpClientFactory httpClientFactory, GoogleVertexExpressLlmOptions? options = null,
        string id = "google-vertex-express", string httpClientName = "Asgard.Loki.LLM.GoogleVertex.Express")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _options = options is null ? new() : options with { }; ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBodyBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBufferedTerminalEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumTrackedCandidates, 0);
        VertexJson.Text(id); VertexJson.Text(httpClientName); _factory = httpClientFactory; _clientName = httpClientName; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":vertex-express-v1:" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.ApiKey;
    /// <summary>原生完整结果通过 EOF、长度、释放和最终取消检查后返回。</summary>
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        JsonElement result;
        using (var message = Prepare(request, context, false, cancellationToken))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await VertexJson.ReadAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, VertexJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Vertex Express 完整调用没有返回 JSON。");
            result = VertexJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex Express 结果必须是原生对象。"); if (IsError(result)) throw Error(response.StatusCode, VertexJson.Decode(bytes));
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(result, context.Metadata);
    }
    /// <summary>复用本供应商的原生 Gemini 状态规则，保留事件/索引/用量并延迟终态尾部。</summary>
    public async IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new VertexStreamState(_options.MaximumTrackedCandidates); var pending = new List<(JsonElement Data, SseFrame Frame)>();
        using (var message = Prepare(request, context, true, cancellationToken))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); throw Error(response.StatusCode, VertexJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); if (IsError(VertexJson.Parse(bytes))) throw Error(response.StatusCode, VertexJson.Decode(bytes)); throw new InvalidDataException("Vertex Express 流式调用没有返回 SSE。");
            }
            using var bounded = new VertexReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var data = VertexJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex Express SSE data 必须是原生对象。"); if (IsError(data)) throw Error(response.StatusCode, frame.Data); state.Observe(data);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Vertex Express 终态尾部暂存数超限。"); pending.Add((data, frame)); }
                else yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
    }
    /// <summary>在创建工厂/首次等待前复制原生报文；Key 仅存在所选请求头中。</summary>
    private static HttpRequestMessage Prepare(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new ArgumentException("Vertex Express 要求显式 API Key 凭据。");
        var key = context.Credential.ApiKey; VertexJson.Text(key); if (key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) throw new ArgumentException("Vertex Express API Key 不能包含空白或控制字符。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0) throw new ArgumentException("Vertex Express 根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        var resource = context.Target.UpstreamModel; ArgumentException.ThrowIfNullOrWhiteSpace(resource); VertexJson.Text(resource); var parts = resource.Split('/');
        if (parts.Length != 4 || parts[0] != "publishers" || parts[1] != "google" || parts[2] != "models" || string.IsNullOrWhiteSpace(parts[3]) || parts[3] is "." or "..") throw new ArgumentException("Vertex Express 要求显式 publishers/google/models/{model} 短资源名。");
        VertexJson.Input(payload); var body = payload.DeepClone().AsObject(); body.Remove("model"); body.Remove("stream");
        var path = string.Join('/', parts.Select(Uri.EscapeDataString)) + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"); var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try { message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); message.Headers.Add("x-goog-api-key", key); message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json")); return message; } catch { message.Dispose(); throw; }
    }
    /// <summary>所选 Key 覆盖同类默认头；不能继承其他认证或项目身份，也不修改借用工厂。</summary>
    private static void BeforeSend(HttpClient client, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("api-key") || client.DefaultRequestHeaders.Contains("x-api-key") || client.DefaultRequestHeaders.Contains("x-goog-user-project")) throw new InvalidOperationException("Vertex Express 默认头含未选择的认证或项目身份。");
    }
    /// <summary>只识别根级原生错误，不解释工具参数中的业务字段。</summary>
    private static bool IsError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
    /// <summary>保留原始状态和错误内容，不触发重试或认证降级。</summary>
    private static LlmUpstreamException Error(System.Net.HttpStatusCode status, string body) => new("Vertex Express 原生调用失败。", status, body);
}
