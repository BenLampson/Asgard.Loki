using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Rerank.SGLang.Internal;

namespace Asgard.Loki.Rerank.SGLang;

/// <summary>执行单次 SGLang 原生重排，不包装数组、重算分数、执行服务或自动重试。</summary>
public sealed class SgLangRerankAdapter : IRerankAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly SgLangRerankOptions _options;
    private readonly Uri _endpoint;
    /// <summary>校验完整固定目标，保留工厂所有权且不访问网络。</summary>
    public SgLangRerankAdapter(IHttpClientFactory factory, SgLangRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.BaseAddress);
        foreach (var text in new[] { options.Id, options.Model, options.HttpClientName }) { ArgumentException.ThrowIfNullOrWhiteSpace(text); SgLangRerankJson.Text(text); }
        if (!Enum.IsDefined(options.Authentication)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Authentication == SgLangRerankAuthentication.Anonymous)
        { if (!string.IsNullOrEmpty(options.ApiKey)) throw new ArgumentException("匿名目标不能同时配置凭据。"); }
        else
        { ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Bearer 凭据必须为无空白/控制字符的 ASCII 文本。"); }
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0) throw new ArgumentException("SGLang 根地址必须为无凭据/查询/片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "rerank"); _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = SgLangRerankPayload.Create(request);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(payload.Body);
        if (requestBytes.Length > _options.MaxRequestBytes) throw new ArgumentException("重排请求超过本地 UTF-8 字节上限。");
        byte[] bytes; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            if (_options.Authentication == SgLangRerankAuthentication.BearerToken) message.Headers.Authorization = new("Bearer", _options.ApiKey);
            message.Headers.Accept.ParseAdd("application/json"); message.Content = new ByteArrayContent(requestBytes); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var client = _factory.CreateClient(_options.HttpClientName);
            foreach (var name in new[] { "Authorization", "api-key", "x-api-key", "OpenAI-Organization", "OpenAI-Project" }) client.DefaultRequestHeaders.Remove(name);
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _endpoint)) throw new InvalidDataException("重排响应的实际目标/方法改变。");
            if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SGLang 重排不接受 SSE。");
            var length = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && length is null || length < 0) throw new InvalidDataException("重排响应声明长度无效。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new RerankUpstreamException("SGLang 重排调用失败。", status, bytes, type);
        var root = SgLangRerankJson.Parse(bytes);
        if (SgLangRerankResponse.IsError(root)) throw new RerankUpstreamException("SGLang 返回原生业务错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("SGLang 重排只接受 UTF-8 JSON。");
        SgLangRerankResponse.Validate(root, payload.Documents, payload.ExpectedCount, payload.ReturnDocuments);
        cancellationToken.ThrowIfCancellationRequested(); return new(root, new(request.Model, _options.Model, Id));
    }
}
