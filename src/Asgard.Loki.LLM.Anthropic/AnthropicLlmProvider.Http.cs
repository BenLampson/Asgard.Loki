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
        token.ThrowIfCancellationRequested(); JsonElement result; HttpResponseMessage? actual = null; AnthropicConfiguredHttpCall? configured;
        using (var request = Request(payload, context, false, out configured))
        using (var client = _factory.CreateClient(_clientName))
        {
            if (configured is null) ValidateDefaults(client); else configured.BeforeSend(client); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            actual = response; configured?.Verify(response);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = await AnthropicHttpJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Error("Claude HTTP 调用失败。", response, bytes, configured is not null);
            result = AnthropicHttpJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude 完整结果必须是 JSON 对象。");
            if (AnthropicHttpJson.IsError(result)) throw Error("Claude 返回原生错误。", response, bytes, configured is not null);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Claude 完整调用没有返回 JSON。");
        }
        token.ThrowIfCancellationRequested(); configured?.Verify(actual); return result;
    }
    /// <summary>原始消息/内容关联由本供应商维护；未知尾帧保留但不能掩盖错误或截断。</summary>
    private async IAsyncEnumerable<SseFrame> StreamFramesAsync(JsonObject payload, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); List<SseFrame> pending = []; var state = new AnthropicStreamState(_options.MaximumTrackedContentBlocks); HttpResponseMessage? actual = null; AnthropicConfiguredHttpCall? configured;
        using (var request = Request(payload, context, true, out configured))
        using (var client = _factory.CreateClient(_clientName))
        {
            if (configured is null) { ValidateDefaults(client); request.Headers.Accept.Add(new("text/event-stream")); } else configured.BeforeSend(client); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            actual = response; configured?.Verify(response);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AnthropicHttpJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
                throw Error("Claude 流式调用失败或未返回 SSE。", response, bytes, configured is not null);
            }
            using var bounded = new AnthropicHttpReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); configured?.Verify(response); var data = AnthropicHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Claude SSE 事件必须是原生 JSON 对象。");
                if (state.Finishing && AnthropicStreamState.Type(data, frame.EventName) == "error") throw Error("Claude 在可能终态后返回错误。", response, Encoding.UTF8.GetBytes(frame.Data), configured is not null);
                state.Observe(data, frame.EventName);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Claude 终态尾部暂存数量超限。"); pending.Add(frame); }
                else yield return frame;
            }
            state.Complete();
        }
        token.ThrowIfCancellationRequested(); configured?.Verify(actual); foreach (var item in pending) { token.ThrowIfCancellationRequested(); yield return item; }
    }
    /// <summary>显式传输保留原始状态、媒体与任意错误字节；默认异常文本契约保持不变。</summary>
    private static LlmUpstreamException Error(string message, HttpResponseMessage response, byte[] bytes, bool raw)
        => raw ? new(message, response.StatusCode, bytes, response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null)
            : new(message, response.StatusCode, AnthropicHttpJson.Decode(bytes));
}
