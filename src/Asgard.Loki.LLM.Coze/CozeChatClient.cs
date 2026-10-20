using System.Net.Http.Headers;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Coze.Internal;

namespace Asgard.Loki.LLM.Coze;

/// <summary>原生固定智能体调用客户端，每个方法一次 HTTP，不建立伪模型路由、自动轮询或本地工作流。</summary>
public sealed partial class CozeChatClient
{
    private readonly IHttpClientFactory _factory;
    private readonly CozeChatTarget _target;
    /// <summary>快照完整服务/Bot/独立令牌身份并借用工厂，不查询配置或借用其他能力的 Key。</summary>
    public CozeChatClient(IHttpClientFactory factory, CozeChatOptions options, string apiToken)
    { ArgumentNullException.ThrowIfNull(factory); _factory = factory; _target = new(options, apiToken); }
    /// <summary>固定原生服务元数据，BotId 不冒充底层实际模型。</summary>
    public CozeChatMetadata Metadata => _target.Metadata;
    /// <summary>发起一次非流式原生对话，立即返回原始回执/状态，不等待生成或设置保存历史默认值。</summary>
    public Task<CozeChatResult> CreateAsync(CozeChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = _target.Create(request, false);
        return InvokeAsync(message, "create", null, request.ConversationId, cancellationToken);
    }
    /// <summary>只查询一次原生 Chat 状态，引用必须保持完整目标及原始 Chat/Conversation 绑定。</summary>
    public Task<CozeChatResult> RetrieveAsync(CozeChatReference reference, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return InvokeAsync(_target.Query(reference, false), "retrieve", reference, null, cancellationToken); }
    /// <summary>只读取一次该 Chat 的完整原生消息数组；不查询整个会话、不选择最后答案或执行工具。</summary>
    public Task<CozeChatResult> GetMessagesAsync(CozeChatReference reference, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return InvokeAsync(_target.Query(reference, true), "messages", reference, null, cancellationToken); }
    /// <summary>读取与全部资源关闭完成后再解释成功数据，不将 code=0 当作模型生成已完成。</summary>
    private async Task<CozeChatResult> InvokeAsync(HttpRequestMessage message, string operation, CozeChatReference? reference, string? conversation, CancellationToken token)
    {
        var endpoint = message.RequestUri!; var endpointText = endpoint.OriginalString; var method = message.Method;
        HttpResponseMessage? response = null; byte[] bytes; string? contentType; System.Net.HttpStatusCode status;
        using (message)
        using (var client = _factory.CreateClient(_target.Options.HttpClientName))
        {
            CozeChatTarget.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                CozeChatTarget.Verify(message, response, endpointText, method); status = response.StatusCode; contentType = ContentType(response);
                if (response.IsSuccessStatusCode && MediaTypeHeaderValue.TryParse(contentType, out var media) && media.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidDataException("Coze 非流式回执/查询不能自动切换 SSE。");
                Length(response, _target.Options.MaximumResponseBytes);
                bytes = await HttpBodyReader.ReadAsync(response.Content, _target.Options.MaximumResponseBytes, token).ConfigureAwait(false); CozeChatTarget.Verify(message, response, endpointText, method);
            }
        }
        token.ThrowIfCancellationRequested(); CozeChatTarget.Verify(message, response, endpointText, method);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Coze 原生 HTTP 调用失败。", status, bytes, contentType);
        var data = CozeJson.Parse(bytes);
        if (CozeChatResponse.Code(data) != 0) throw new LlmUpstreamException("Coze 返回原生错误 code。", status, bytes, contentType);
        Media(contentType, false);
        if (operation == "messages") CozeChatResponse.Messages(data, _target, reference!);
        else reference = CozeChatResponse.Chat(data, _target, reference, conversation);
        var result = new CozeChatResult(operation, endpoint, status, contentType, data, _target.Metadata, reference!); token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原始媒体值保留参数和缺省，不有损解码错误内容。</summary>
    private static string? ContentType(HttpResponseMessage response) => response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>成功结果必须为原生对应的 UTF-8 JSON/SSE，不猜测另一协议。</summary>
    private static void Media(string? type, bool stream)
    {
        if (type is null) return;
        if (!MediaTypeHeaderValue.TryParse(type, out var media) || media.MediaType is not { } value
            || !(stream ? value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) : value.Equals("application/json", StringComparison.OrdinalIgnoreCase) || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Coze 成功结果需要对应的 UTF-8 JSON/SSE 媒体。");
    }
    /// <summary>坏长度与超预算声明在首次读取之前失败。</summary>
    private static long? Length(HttpResponseMessage response, long maximum)
    {
        var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
        if (declared && length is null || length < 0 || length > maximum) throw new InvalidDataException("Coze 原生 Content-Length 无效或超过预算。");
        return length;
    }
}
