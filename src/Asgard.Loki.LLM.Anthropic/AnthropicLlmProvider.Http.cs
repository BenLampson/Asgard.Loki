using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Anthropic.Internal;

namespace Asgard.Loki.LLM.Anthropic;

/// <summary>单次原生 Messages 传输；终态等待 EOF、长度、异步及同步释放和最终取消。</summary>
public sealed partial class AnthropicLlmProvider
{
    /// <summary>完整原生结果和错误均有界严格读取，不替换未知字段或自动重试。</summary>
    private async Task<JsonElement> SendAsync(JsonObject payload, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); JsonElement result;
        using (var request = Request(payload, context))
        using (var client = _factory.CreateClient(_clientName))
        {
            ValidateDefaults(client); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = await AnthropicHttpJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Claude HTTP 调用失败。", response.StatusCode, AnthropicHttpJson.Decode(bytes));
            result = AnthropicHttpJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude 完整结果必须是 JSON 对象。");
            if (AnthropicHttpJson.IsError(result)) throw new LlmUpstreamException("Claude 返回原生错误。", response.StatusCode, AnthropicHttpJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Claude 完整调用没有返回 JSON。");
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原始消息/内容关联由本供应商维护；未知尾帧保留但不能掩盖错误或截断。</summary>
    private async IAsyncEnumerable<SseFrame> StreamFramesAsync(JsonObject payload, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); List<SseFrame> pending = []; var state = new AnthropicStreamState(_options.MaximumTrackedContentBlocks);
        using (var request = Request(payload, context))
        using (var client = _factory.CreateClient(_clientName))
        {
            ValidateDefaults(client); token.ThrowIfCancellationRequested(); request.Headers.Accept.Add(new("text/event-stream"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AnthropicHttpJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                throw new LlmUpstreamException("Claude 流式调用失败或未返回 SSE。", response.StatusCode, AnthropicHttpJson.Decode(bytes));
            }
            using var bounded = new AnthropicHttpReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); var data = AnthropicHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude SSE 事件必须是原生 JSON 对象。");
                if (state.Finishing && AnthropicStreamState.Type(data, frame.EventName) == "error") throw new LlmUpstreamException("Claude 在可能终态后返回错误。", response.StatusCode, frame.Data);
                state.Observe(data, frame.EventName);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Claude 终态尾部暂存数量超限。"); pending.Add(frame); }
                else yield return frame;
            }
            state.Complete();
        }
        token.ThrowIfCancellationRequested(); foreach (var item in pending) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
