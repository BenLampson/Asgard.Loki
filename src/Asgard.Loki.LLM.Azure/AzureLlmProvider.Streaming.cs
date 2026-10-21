using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>原生 SSE 单次创建传输；终态经过 EOF 和释放后再交付。</summary>
public sealed partial class AzureLlmProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamProtocolAsync(request, context, false, cancellationToken).ConfigureAwait(false)) yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds);
    }
    /// <inheritdoc />
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamProtocolAsync(request, context, true, cancellationToken).ConfigureAwait(false)) yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds);
    }
    /// <summary>保留先前增量；首个可能终态后的有限尾部按原始顺序暂存，不自动重连。</summary>
    private async IAsyncEnumerable<(JsonElement Data, SseFrame Frame)> StreamProtocolAsync(JsonObject payload, LlmAdapterContext context, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        var pending = new List<(JsonElement Data, SseFrame Frame)>(); var chat = new AzureChatStreamState(_options.MaximumTrackedChoices); var nativeResponse = new AzureResponseStreamState(); var done = false;
        using (var request = await PrepareAsync(payload, context, responses, true, token).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, request, context, token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await AzureJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                throw new LlmUpstreamException("Azure 原生流式调用失败。", response.StatusCode, AzureJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AzureJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                if (AzureJson.IsError(AzureJson.Parse(bytes))) throw new LlmUpstreamException("Azure 流式调用返回原生错误。", response.StatusCode, AzureJson.Decode(bytes));
                throw new InvalidDataException("Azure 流式调用没有返回 SSE。");
            }
            using var bounded = new AzureReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (done) throw new InvalidDataException("Azure SSE 在 DONE 后追加事件。");
                if (frame.Data == "[DONE]") { if (responses) nativeResponse.Complete(); else chat.Complete(); done = true; continue; }
                var data = AzureJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure SSE data 必须是原生 JSON 对象。");
                if (AzureJson.IsError(data)) throw new LlmUpstreamException("Azure SSE 返回原生错误。", response.StatusCode, frame.Data);
                if (responses) nativeResponse.Observe(data, frame.EventName); else chat.Observe(data);
                if (responses ? nativeResponse.Terminal : chat.Finishing)
                {
                    if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Azure SSE 终态尾部暂存数超限。");
                    pending.Add((data, frame));
                }
                else yield return (data, frame);
            }
            if (responses) nativeResponse.Complete(); else { if (!done) throw new InvalidDataException("Azure Chat SSE 缺少 DONE。"); chat.Complete(); }
        }
        token.ThrowIfCancellationRequested();
        foreach (var item in pending) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
