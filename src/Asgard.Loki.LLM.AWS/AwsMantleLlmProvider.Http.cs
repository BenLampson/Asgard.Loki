using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Mantle 原生 JSON/SSE 使用 AWS 包自己的签名与有界流状态，不依赖其他供应商。</summary>
public sealed partial class AwsMantleLlmProvider
{
    /// <inheritdoc />
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, context, false, cancellationToken).ConfigureAwait(false); JsonElement result;
        using (var message = prepared.Message)
        using (var client = _factory.CreateClient(_clientName))
        {
            SignBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Mantle Messages 调用失败。", response.StatusCode, AwsJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mantle 完整调用未返回 JSON。");
            result = AwsJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Mantle 完整结果必须是原生 JSON 对象。");
            if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null || result.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "error") throw new LlmUpstreamException("Mantle 返回原生错误。", response.StatusCode, AwsJson.Decode(bytes));
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(result, context.Metadata);
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, context, true, cancellationToken).ConfigureAwait(false); var state = new AwsClaudeStreamState(_options.MaximumTrackedContentBlocks); List<ClaudeMessageEvent> pending = [];
        using (var message = prepared.Message)
        using (var client = _factory.CreateClient(_clientName))
        {
            SignBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
                throw new LlmUpstreamException("Mantle Messages 流失败或未返回 SSE。", response.StatusCode, AwsJson.Decode(bytes));
            }
            using var bounded = new AwsSseReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var data = AwsJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Mantle SSE 事件必须是原生 JSON 对象。");
                if (state.Finishing && AwsClaudeStreamState.Type(data, frame.EventName) == "error") throw new LlmUpstreamException("Mantle 在可能终态后返回原生错误。", response.StatusCode, frame.Data);
                state.Observe(data, frame.EventName); var item = new ClaudeMessageEvent(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Mantle SSE 终态尾部暂存数超限。"); pending.Add(item); }
                else yield return item;
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }
}
