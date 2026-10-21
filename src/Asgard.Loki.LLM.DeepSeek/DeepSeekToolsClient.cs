using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.DeepSeek.Internal;

namespace Asgard.Loki.LLM.DeepSeek;

/// <summary>显式调用 DeepSeek 原生 Search/Fetch；固定服务/凭据，不创建模型 Session、网页代理或工具执行循环。</summary>
public sealed class DeepSeekToolsClient
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _key;
    private readonly string _client;
    private readonly int _requestMaximum;
    private readonly int _responseMaximum;
    /// <summary>捕获完整独立配置并借用工厂；每次仅发送一个原生请求，不发现或借用其他能力凭据。</summary>
    public DeepSeekToolsClient(IHttpClientFactory factory, DeepSeekToolsOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.BaseAddress);
        var root = options.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("DeepSeek tools 根必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); DeepSeekToolsJson.Text(options.ApiKey);
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("API Key 必须为无空白/控制字符的 ASCII 文本。");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName); DeepSeekToolsJson.Text(options.HttpClientName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(root.AbsoluteUri.TrimEnd('/') + "/"); _key = options.ApiKey; _client = options.HttpClientName; _requestMaximum = options.MaximumRequestBytes; _responseMaximum = options.MaximumResponseBytes;
    }
    /// <summary>执行一次原生搜索并保留结果顺序、评分和完整内容；不自动获取页面或调用生成模型。</summary>
    public Task<DeepSeekToolsResult> SearchAsync(JsonObject parameters, CancellationToken cancellationToken = default) => InvokeAsync(parameters, false, cancellationToken);
    /// <summary>把原生 URL 交给固定的 DeepSeek fetch API；SDK 本地不访问页面或解析/执行 Markdown。</summary>
    public Task<DeepSeekToolsResult> FetchAsync(JsonObject parameters, CancellationToken cancellationToken = default) => InvokeAsync(parameters, true, cancellationToken);
    /// <summary>首次传输等待前冻结所有请求，资源/EOF/取消校验完成后才交付完整原生结果。</summary>
    private async Task<DeepSeekToolsResult> InvokeAsync(JsonObject parameters, bool fetch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var body = DeepSeekToolsPayload.Snapshot(parameters, fetch, _requestMaximum); var operation = fetch ? "fetch" : "search"; var endpoint = new Uri(_root, operation);
        byte[] bytes; HttpStatusCode status; string? type;
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        using (var client = _factory.CreateClient(_client))
        {
            token.ThrowIfCancellationRequested();
            if (new[] { "api-key", "x-api-key", "x-goog-api-key" }.Any(client.DefaultRequestHeaders.Contains)) throw new InvalidOperationException("工具客户端含未选认证方式的默认头。");
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; request.Headers.Accept.ParseAdd("application/json"); request.Headers.Authorization = new("Bearer", _key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post || request.RequestUri != endpoint || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != endpoint)) throw new InvalidDataException("DeepSeek 工具实际方法或目标发生改变。");
            type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
            if (response.IsSuccessStatusCode && MediaTypeHeaderValue.TryParse(type, out var media) && media.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true) throw new InvalidDataException("原生 Search/Fetch 不支持 SSE。");
            var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
            if (declared && length is null || length < 0 || length > _responseMaximum) throw new InvalidDataException("DeepSeek 工具声明长度无效或超过上限。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseMaximum, token).ConfigureAwait(false); status = response.StatusCode;
        }
        token.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("DeepSeek 原生工具 HTTP 调用失败。", status, bytes, type);
        JsonElement data; try { data = DeepSeekToolsJson.Parse(bytes); } catch (DecoderFallbackException error) { throw new InvalidDataException("DeepSeek 工具正文不是有效 UTF-8。", error); }
        if (DeepSeekToolsJson.Error(data)) throw new LlmUpstreamException("DeepSeek 返回原生工具错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) && !header.MediaType!.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("DeepSeek 工具成功结果必须为 UTF-8 JSON。");
        DeepSeekToolsResponse.Validate(data, fetch); var result = new DeepSeekToolsResult(operation, endpoint, status, type, data); token.ThrowIfCancellationRequested(); return result;
    }
}
