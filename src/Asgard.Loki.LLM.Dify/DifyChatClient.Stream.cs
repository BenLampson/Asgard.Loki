using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Dify.Internal;

namespace Asgard.Loki.LLM.Dify;

/// <summary>原生流的业务状态由供应商独立维护，公共基础设施只提供有界传输。</summary>
public sealed partial class DifyChatClient
{
    /// <summary>一次 chat-messages SSE，保留完整原生数据及终态尾流，不聚合回答、执行工具或自动恢复。</summary>
    public async IAsyncEnumerable<DifyChatEvent> StreamAsync(DifyChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var message = _target.Create(request, true, out var conversation); var endpoint = message.RequestUri!.OriginalString;
        HttpResponseMessage? response = null; List<DifyChatEvent> tail = []; var state = new DifyChatStreamState(_target, conversation);
        using (message)
        using (var client = _factory.CreateClient(_target.Options.HttpClientName))
        {
            DifyChatTarget.Defaults(client);
            using (response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                DifyChatTarget.Verify(message, response, endpoint); var contentType = ContentType(response);
                if (!response.IsSuccessStatusCode || contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var media)
                    && (media.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true || media.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true))
                {
                    Length(response, _target.Options.MaximumResponseBytes); var bytes = await HttpBodyReader.ReadAsync(response.Content, _target.Options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || DifyChatResponse.Error(DifyJson.Parse(bytes))) throw new LlmUpstreamException("Dify 原生流返回 HTTP/原生 JSON 错误。", response.StatusCode, bytes, contentType);
                    throw new InvalidDataException("Dify 流式调用不能自动接受阻塞 JSON 响应。");
                }
                Media(contentType, true); var expected = Length(response, _target.Options.MaximumStreamBytes);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await foreach (var frame in SseReader.ReadBoundedAsync(source, _target.Options.MaximumStreamBytes, cancellationToken, strictUtf8: true, expectedLength: expected).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); DifyChatTarget.Verify(message, response, endpoint);
                        var item = state.Push(frame, response.StatusCode, contentType);
                        if (state.HoldTail)
                        {
                            if (tail.Count >= _target.Options.MaximumBufferedTailFrames) throw new InvalidDataException("Dify 生成结束尾部超过显式帧数上限。");
                            tail.Add(item);
                        }
                        else { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
                    }
                    state.Complete();
                }
                DifyChatTarget.Verify(message, response, endpoint);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); DifyChatTarget.Verify(message, response, endpoint);
        foreach (var item in tail) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
