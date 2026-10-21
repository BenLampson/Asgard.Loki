using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Bedrock 二进制 EventStream 与 Claude 原生状态独立验证；成功尾部通过 EOF/释放后交付。</summary>
public sealed partial class AwsLlmProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new AwsClaudeStreamState(_options.MaximumTrackedClaudeBlocks); var pending = new List<ClaudeMessageEvent>(); var prepared = await PrepareAsync(request, context, true, cancellationToken).ConfigureAwait(false);
        using (var message = prepared.Request)
        using (var client = _factory.CreateClient(_clientName))
        {
            AuthenticateBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); throw new LlmUpstreamException("Bedrock 原生流式调用失败。", response.StatusCode, AwsJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/vnd.amazon.eventstream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); var data = AwsJson.Parse(bytes); if (IsStreamError(data)) throw new LlmUpstreamException("Bedrock 流式调用返回原生错误。", response.StatusCode, AwsJson.Decode(bytes)); throw new InvalidDataException("Bedrock 流式调用没有返回原生二进制 EventStream。");
            }
            if (response.Headers.TryGetValues("x-amzn-bedrock-content-type", out var contentTypes))
            {
                var values = contentTypes.ToArray(); if (values.Length != 1 || !JsonMedia(values[0])) throw new InvalidDataException("Bedrock Claude 的流内模型内容不是 JSON。");
            }
            await foreach (var frame in AwsEventStreamReader.ReadAsync(source, _options.MaximumEventStreamFrameBytes, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken, _options.MaximumEventStreamHeaders).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var item = DecodeStreamEvent(frame, state, context, response.StatusCode);
                if (state.Finishing)
                {
                    if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw AwsEventStreamExtensions.Attach(new InvalidDataException("Bedrock Claude 终态尾部暂存数超限。"), frame); pending.Add(item);
                }
                else yield return item;
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }
    /// <summary>外层错误及所有未知元数据可通过帧视图取得，原生 Claude JSON 不注入合成字段。</summary>
    private static ClaudeMessageEvent DecodeStreamEvent(AwsEventStreamFrame frame, AwsClaudeStreamState state, LlmAdapterContext context, System.Net.HttpStatusCode status)
    {
        try
        {
            var kind = frame.StringHeader(":message-type"); var payload = frame.Payload.ToArray();
            if (kind is "exception" or "error")
            {
                if (kind == "exception") _ = frame.StringHeader(":exception-type"); else { _ = frame.StringHeader(":error-code"); _ = frame.StringHeader(":error-message"); }
                throw new LlmUpstreamException("Bedrock EventStream 返回原生传输错误。", status, AwsJson.Decode(payload));
            }
            if (kind != "event") throw new InvalidDataException("Bedrock EventStream 消息类型无效。"); var eventType = frame.StringHeader(":event-type"); if (eventType.Length == 0) throw new InvalidDataException("Bedrock EventStream 缺少原生事件名。");
            var media = frame.StringHeader(":content-type", required: false); if (media.Length != 0 && !JsonMedia(media)) throw new NotSupportedException("Bedrock 未知二进制事件不能隐式转换为 Claude JSON，可读取原始帧。");
            var data = AwsJson.Parse(payload); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock EventStream 载荷必须是原生 JSON 对象。");
            if (eventType == "chunk")
            {
                if (!data.TryGetProperty("bytes", out var blob) || blob.ValueKind != JsonValueKind.String) throw new InvalidDataException("Bedrock chunk 缺少原生 Base64 bytes。"); data = AwsJson.Parse(blob.GetBytesFromBase64()); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock Claude chunk 必须是原生 JSON 对象。");
                if (IsStreamError(data) && (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "error" || state.Finishing)) throw new LlmUpstreamException("Bedrock Claude 流返回迟到或平台错误。", status, AwsJson.Decode(blob.GetBytesFromBase64()));
                state.Observe(data, null);
            }
            return AwsEventStreamExtensions.Attach(new ClaudeMessageEvent(data, context.Metadata, eventType), frame);
        }
        catch (Exception error) { AwsEventStreamExtensions.Attach(error, frame); throw; }
    }
    /// <summary>模型和 JSON 事件的内容类型允许参数，不根据声明切换到其他编码。</summary>
    private static bool JsonMedia(string text) => System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(text, out var media) && (string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) || media.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true);
    /// <summary>只识别根级平台错误，不解释工具参数中的同名业务值。</summary>
    private static bool IsStreamError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
