using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Internal;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>MaaS 原生增量传输，首个终态后的有限尾部通过完整来源结束和释放后交付。</summary>
public sealed partial class GoogleVertexMaaSLlmProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new VertexMaaSStreamState(_options.MaximumTrackedChoices, _options.MaximumTrackedToolCalls); var pending = new List<(JsonElement Data, SseFrame Frame)>(); var done = false;
        using (var message = await PrepareAsync(request, context, true, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, message, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); throw new LlmUpstreamException("Vertex MaaS 原生流式调用失败。", response.StatusCode, VertexJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await VertexJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); var data = VertexJson.Parse(bytes);
                if (IsStreamError(data)) throw new LlmUpstreamException("Vertex MaaS 流式调用返回原生错误。", response.StatusCode, VertexJson.Decode(bytes)); throw new InvalidDataException("Vertex MaaS 流式调用没有返回 SSE。");
            }
            using var bounded = new VertexReadStream(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength);
            await foreach (var frame in SseReader.ReadAsync(bounded, cancellationToken, strictUtf8: true).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); if (done) throw new InvalidDataException("Vertex MaaS 在 DONE 后追加事件。");
                if (frame.Data == "[DONE]") { state.Complete(); done = true; continue; }
                var data = VertexJson.Parse(Encoding.UTF8.GetBytes(frame.Data)); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex MaaS SSE data 必须是原生对象。");
                if (IsStreamError(data) || frame.EventName == "error") throw new LlmUpstreamException("Vertex MaaS SSE 返回原生错误。", response.StatusCode, frame.Data); state.Observe(data);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw new InvalidDataException("Vertex MaaS 终态尾部暂存数超限。"); pending.Add((data, frame)); }
                else yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            }
            if (!done) throw new InvalidDataException("Vertex MaaS SSE 缺少 DONE。"); state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
    }
    /// <summary>只识别根级原生平台错误，不解释工具参数中的同名业务字段。</summary>
    private static bool IsStreamError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
