using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Rerank.SiliconFlow.Internal;

namespace Asgard.Loki.Rerank.SiliconFlow;

/// <summary>单次原生 SiliconFlow 重排，不转换文档、补默认值、重试或更换目标。</summary>
public sealed class SiliconFlowRerankAdapter : IRerankAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly SiliconFlowRerankOptions _options;
    private readonly Uri _endpoint;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>只校验固定完整目标；HTTP 工厂与处理器均保持调用方所有权。</summary>
    public SiliconFlowRerankAdapter(IHttpClientFactory factory, SiliconFlowRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName); ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("重排根地址必须为无凭据、查询参数或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "rerank"); _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = SiliconFlowRerankPayload.Create(request, _options.Model);
        RerankResult result;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey); message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (response.IsSuccessStatusCode && media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase)
                && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SiliconFlow 重排只接受完整 JSON 响应。");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
            string body;
            try { body = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("SiliconFlow 响应不是有效 UTF-8。", error); }
            if (!response.IsSuccessStatusCode) throw new RerankUpstreamException("SiliconFlow 重排请求失败。", (int)response.StatusCode, body);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
            if (SiliconFlowRerankResponse.IsError(root)) throw new RerankUpstreamException("SiliconFlow 返回原生业务错误。", (int)response.StatusCode, body);
            SiliconFlowRerankResponse.Validate(root, payload); result = new(root, new(request.Model, _options.Model, Id));
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
