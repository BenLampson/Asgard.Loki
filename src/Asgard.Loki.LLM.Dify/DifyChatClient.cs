using System.Net.Http.Headers;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Dify.Internal;

namespace Asgard.Loki.LLM.Dify;

/// <summary>固定原生应用的单次 chat-messages 客户端；不托管工作流、轮询、模型选择或文件管理。</summary>
public sealed partial class DifyChatClient
{
    private readonly IHttpClientFactory _factory;
    private readonly DifyChatTarget _target;
    /// <summary>固定完整原生应用目标，借用工厂并独立保存调用者明确提供的应用 Key。</summary>
    public DifyChatClient(IHttpClientFactory factory, DifyChatOptions options, string apiKey)
    { ArgumentNullException.ThrowIfNull(factory); _factory = factory; _target = new(options, apiKey); }
    /// <summary>完整应用目标元数据，不包含密钥或虚构底层模型。</summary>
    public DifyChatMetadata Metadata => _target.Metadata;
    /// <summary>一次原生阻塞请求，Agent 明确不支持；保留完整消息或 Chatflow 暂停原文。</summary>
    public Task<DifyChatResult> SendAsync(DifyChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = _target.Create(request, false, out var conversation);
        return InvokeAsync(message, conversation, cancellationToken);
    }
    /// <summary>拥有的资源全部关闭并完成取消检查后，才交付独立原生结果。</summary>
    private async Task<DifyChatResult> InvokeAsync(HttpRequestMessage message, string? conversation, CancellationToken token)
    {
        var endpoint = message.RequestUri!; var endpointText = endpoint.OriginalString;
        HttpResponseMessage? response = null; byte[] bytes; string? contentType; System.Net.HttpStatusCode status;
        using (message)
        using (var client = _factory.CreateClient(_target.Options.HttpClientName))
        {
            DifyChatTarget.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                DifyChatTarget.Verify(message, response, endpointText); status = response.StatusCode; contentType = ContentType(response);
                Length(response, _target.Options.MaximumResponseBytes);
                bytes = await HttpBodyReader.ReadAsync(response.Content, _target.Options.MaximumResponseBytes, token).ConfigureAwait(false);
                DifyChatTarget.Verify(message, response, endpointText);
            }
        }
        token.ThrowIfCancellationRequested(); DifyChatTarget.Verify(message, response, endpointText);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Dify 原生 HTTP 调用失败。", status, bytes, contentType);
        var data = DifyJson.Parse(bytes);
        if (DifyChatResponse.Error(data)) throw new LlmUpstreamException("Dify 返回原生错误。", status, bytes, contentType);
        Media(contentType, false); DifyChatResponse.Blocking(data, _target.Options.Profile, conversation);
        var result = new DifyChatResult(endpoint, status, contentType, data, _target.Metadata); token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>保留原始媒体头，包括参数和缺省。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>成功数据需要原生 UTF-8 JSON/SSE，不自动转换另一媒体。</summary>
    private static void Media(string? type, bool stream)
    {
        if (type is null) return;
        if (!MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(stream ? value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) : value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dify 成功响应需要对应的 UTF-8 JSON/SSE 媒体。");
    }
    /// <summary>首次读取前验证声明长度和本地预算。</summary>
    private static long? Length(HttpResponseMessage response, long maximum)
    {
        var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
        if (declared && length is null || length < 0 || length > maximum) throw new InvalidDataException("Dify Content-Length 无效或超过预算。");
        return length;
    }
}
