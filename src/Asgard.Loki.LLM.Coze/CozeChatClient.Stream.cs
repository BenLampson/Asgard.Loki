using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Coze.Internal;

namespace Asgard.Loki.LLM.Coze;

/// <summary>原生 SSE 使用供应商独立状态，不与模型/音频能力共享业务协调层。</summary>
public sealed partial class CozeChatClient
{
    /// <summary>执行一次原生流式调用，保留两级事件、原生 DONE 与未知数据；不拼接回答或自动执行工具。</summary>
    public async IAsyncEnumerable<CozeChatEvent> StreamAsync(CozeChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = _target.Create(request, true); var endpointText = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; List<CozeChatEvent> tail = []; var state = new CozeChatStreamState(_target, request.ConversationId);
        using (message)
        using (var client = _factory.CreateClient(_target.Options.HttpClientName))
        {
            CozeChatTarget.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                CozeChatTarget.Verify(message, response, endpointText, HttpMethod.Post); var contentType = ContentType(response);
                if (!response.IsSuccessStatusCode || contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var media)
                    && (media.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true || media.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true))
                {
                    Length(response, _target.Options.MaximumResponseBytes); var bytes = await HttpBodyReader.ReadAsync(response.Content, _target.Options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || CozeChatResponse.Code(CozeJson.Parse(bytes)) != 0) throw new LlmUpstreamException("Coze 原生流返回 HTTP/原生 JSON 错误。", response.StatusCode, bytes, contentType);
                    throw new InvalidDataException("Coze 原生流不能把 JSON 回执当作完整 SSE 或自动转为轮询。");
                }
                Media(contentType, true); var expected = Length(response, _target.Options.MaximumStreamBytes);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await foreach (var frame in SseReader.ReadBoundedAsync(source, _target.Options.MaximumStreamBytes, cancellationToken, strictUtf8: true, expectedLength: expected).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); CozeChatTarget.Verify(message, response, endpointText, HttpMethod.Post);
                        var item = state.Push(frame, response.StatusCode, contentType);
                        if (state.HoldTail)
                        {
                            if (tail.Count >= _target.Options.MaximumBufferedTailFrames) throw new InvalidDataException("Coze Chat 终态尾部超过显式帧数上限。");
                            tail.Add(item);
                        }
                        else { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
                    }
                    state.Complete();
                }
                CozeChatTarget.Verify(message, response, endpointText, HttpMethod.Post);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); CozeChatTarget.Verify(message, response, endpointText, HttpMethod.Post);
        foreach (var item in tail) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
