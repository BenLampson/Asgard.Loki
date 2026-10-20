using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>文本补全原生 SSE：选择项的首个结束后保留完整尾部，等待 DONE、EOF 和所有资源关闭。</summary>
public sealed partial class AzureLlmProvider
{
    /// <inheritdoc />
    async IAsyncEnumerable<TextCompletionEvent> ITextCompletionsAdapter.StreamAsync(JsonObject payload, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new List<(JsonElement Data, SseFrame Frame)>(); var state = new AzureTextCompletionStreamState(_options.MaximumTrackedChoices); var done = false; DateTimeOffset? expiry = null;
        using (var request = await PrepareTextAsync(payload, context, true, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            var expected = request.RequestUri; BeforeSend(client, request, context, cancellationToken); CheckTextDefaults(client); if (request.Options.TryGetValue(TokenExpiry, out var acquired)) expiry = acquired;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); CheckTextResponse(request, expected, response); cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry); var type = TextContentType(response);
            var header = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(header?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumBodyBytes, cancellationToken).ConfigureAwait(false); _ = ParseTextBody(bytes, response.StatusCode, type); throw new InvalidDataException("原生文本补全流没有返回 SSE。");
            }
            if (!TextCharset(header!)) throw new InvalidDataException("原生文本补全 SSE 必须为 UTF-8。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bounded = new AzureReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry); if (done) throw new InvalidDataException("文本补全在 DONE 后追加数据帧。");
                if (frame.Data == "[DONE]") { state.Complete(); done = true; continue; }
                var data = AzureJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生文本补全 SSE 必须为 JSON 对象。");
                // 未知 SSE 事件中的 code/message 是业务字段；平铺错误必须有明确 error 事件名。
                if (AzureJson.IsError(data) || frame.EventName == "error" && AzureTextCompletionJson.Error(data)) throw new LlmUpstreamException("Azure SSE 返回原生文本补全错误。", response.StatusCode, Encoding.UTF8.GetBytes(frame.Data), type);
                state.Observe(data);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("文本补全终态尾部暂存数超限。"); pending.Add((data, frame)); }
                else { cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry); yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
            }
            if (!done) throw new InvalidDataException("原生文本补全 SSE 缺少 DONE。"); state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry);
        foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
    }
}
