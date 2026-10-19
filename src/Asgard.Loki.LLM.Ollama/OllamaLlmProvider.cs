using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Internal;

namespace Asgard.Loki.LLM.Ollama;

/// <summary>实现 Ollama 原生 Chat/generate JSON/NDJSON，不启动服务器、管理模型、执行工具或转换 OpenAI 外壳。</summary>
public sealed partial class OllamaLlmProvider : IOllamaChatAdapter, IOllamaGenerateAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly OllamaLlmOptions _options;
    /// <summary>借用命名 HTTP 工厂，配置快照和本地资源限制参与 Session 实现身份。</summary>
    public OllamaLlmProvider(IHttpClientFactory factory, string id = "ollama", string httpClientName = "Asgard.Loki.LLM.Ollama", OllamaLlmOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        options = options is null ? new() : options with { };
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamRecordBytes, 0);
        _factory = factory; _clientName = httpClientName; _options = options; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => $"{GetType().AssemblyQualifiedName}:ollama-chat-v1:generate-v1:{_clientName}:{JsonSerializer.Serialize(_options)}";
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind is LlmCredentialKind.ApiKey or LlmCredentialKind.Anonymous;

    /// <inheritdoc />
    public async Task<OllamaChatResponse> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); OllamaChatResponse result;
        using (var message = Request(request, context, false))
        using (var client = _factory.CreateClient(_clientName))
        {
            Authentication(client, context);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !IsJson(media)) throw new InvalidDataException("完整 Ollama 调用需要单个 JSON 响应。");
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await OllamaJson.ReadAsync(source, _options.MaximumResponseBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false);
            var data = OllamaJson.Parse(bytes);
            if (OllamaJson.IsError(data)) throw new LlmUpstreamException("Ollama 返回原生错误。", response.StatusCode, OllamaJson.Text(bytes));
            var state = new OllamaChatState(); state.Push(data); state.Complete(); result = new(data, context.Metadata);
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OllamaChatEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); OllamaChatEvent? terminal = null; var state = new OllamaChatState();
        using (var message = Request(request, context, true))
        using (var client = _factory.CreateClient(_clientName))
        {
            Authentication(client, context);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !IsJson(media) && !string.Equals(media, "application/x-ndjson", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(media, "application/ndjson", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ollama 流需要原生 NDJSON 或单个原生 JSON 终态。");
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (media is not null && IsJson(media))
            {
                // 原生空消息 load/unload 可直接返回 JSON；不将它重新编码成另一协议。
                var data = OllamaJson.Parse(await OllamaJson.ReadAsync(source, _options.MaximumStreamRecordBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false));
                if (!state.Push(data)) throw new InvalidDataException("单个 Ollama JSON 响应没有原生终态。");
                terminal = new(data, context.Metadata);
            }
            else
                await foreach (var data in OllamaNdjson.ReadAsync(source, _options.MaximumStreamRecordBytes, cancellationToken, response.Content.Headers.ContentLength).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested(); var last = state.Push(data); var item = new OllamaChatEvent(data, context.Metadata);
                    if (last) terminal = item; else yield return item;
                }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is null) throw new InvalidDataException("Ollama 流缺少完整终态。");
        yield return terminal;
    }

    /// <summary>固定实际模型和方法流标志，其余原生字段深拷贝；不解析或重写消息/工具/媒体。</summary>
    private HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context, bool stream, string operation = "chat")
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        var target = context.Target; ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); ArgumentNullException.ThrowIfNull(target.BaseAddress);
        if (!target.BaseAddress.IsAbsoluteUri || target.BaseAddress.Scheme is not ("http" or "https")
            || target.BaseAddress.UserInfo.Length != 0 || target.BaseAddress.Query.Length != 0 || target.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("Ollama 根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。");
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Ollama 目标认证方式未受支持。");
        if (operation == "chat" && (payload["messages"] is not JsonArray messages || messages.Any(item => item is not JsonObject))) throw new ArgumentException("Ollama messages 必须为原生对象数组。");
        OllamaJson.ValidateInput(payload); _ = new UTF8Encoding(false, true).GetByteCount(target.UpstreamModel);
        var body = payload.DeepClone().AsObject(); body["model"] = target.UpstreamModel; body["stream"] = stream;
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(target.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), operation));
        if (context.Credential.Kind == LlmCredentialKind.ApiKey) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.ApiKey);
        request.Headers.Accept.Add(new(stream ? "application/x-ndjson" : "application/json")); request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); return request;
    }
    /// <summary>无认证不能偷偷继承工厂默认 Authorization；不删除调用者配置或把默认 Key 视为选中身份。</summary>
    private static void Authentication(HttpClient client, LlmAdapterContext context)
    {
        if (context.Credential.Kind == LlmCredentialKind.Anonymous && client.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("无认证 Ollama 目标不能继承 HTTP 客户端的默认 Authorization。");
    }
    /// <summary>HTTP 错误保留完整有界原文，不自动重试或猜测另一协议。</summary>
    private async Task ErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await OllamaJson.ReadAsync(source, _options.MaximumResponseBytes, token, response.Content.Headers.ContentLength).ConfigureAwait(false);
        throw new LlmUpstreamException("Ollama 原生 Chat 调用失败。", response.StatusCode, OllamaJson.Text(bytes));
    }
    /// <summary>完整 JSON 与 JSON 扩展媒体保持单个原生对象语义。</summary>
    private static bool IsJson(string media) => string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
}
