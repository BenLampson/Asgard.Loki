using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Converse 原生事件直接来自二进制帧 JSON，不套用 Claude 的 Base64 chunk 和版本策略。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>返回原生事件载荷/名称/完整帧；messageStop 和尾部通过 metadata、EOF 与释放后交付。</summary>
    public async IAsyncEnumerable<BedrockConverseEvent> ConverseStreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new AwsConverseStreamState(_options.MaximumTrackedConverseBlocks ?? 128); var pending = new List<BedrockConverseEvent>(); var prepared = await PrepareAsync(request, context, true, cancellationToken, converse: true).ConfigureAwait(false);
        using (var message = prepared.Request)
        using (var client = _factory.CreateClient(_clientName))
        {
            AuthenticateBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); throw new LlmUpstreamException("Bedrock ConverseStream 调用失败。", response.StatusCode, AwsJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/vnd.amazon.eventstream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); var data = AwsJson.Parse(bytes); if (IsStreamError(data)) throw new LlmUpstreamException("Bedrock ConverseStream 返回原生平台错误。", response.StatusCode, AwsJson.Decode(bytes)); throw new InvalidDataException("Bedrock ConverseStream 没有返回原生二进制事件流。");
            }
            await foreach (var frame in AwsEventStreamReader.ReadAsync(source, _options.MaximumEventStreamFrameBytes, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken, _options.MaximumEventStreamHeaders).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var item = DecodeConverseEvent(frame, state, context, response.StatusCode);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw AwsEventStreamExtensions.Attach(new InvalidDataException("Bedrock ConverseStream 终态尾部暂存数超限。"), frame); pending.Add(item); }
                else yield return item;
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }
    /// <summary>只按原生传输名称解释 Converse 事件，不把任意载荷 type/error 字段当作 Claude 事件。</summary>
    private static BedrockConverseEvent DecodeConverseEvent(AwsEventStreamFrame frame, AwsConverseStreamState state, LlmAdapterContext context, System.Net.HttpStatusCode status)
    {
        try
        {
            var kind = frame.StringHeader(":message-type"); var payload = frame.Payload.ToArray();
            if (kind is "exception" or "error")
            {
                if (kind == "exception") _ = frame.StringHeader(":exception-type"); else { _ = frame.StringHeader(":error-code"); _ = frame.StringHeader(":error-message"); } throw new LlmUpstreamException("Bedrock ConverseStream 返回原生传输错误。", status, AwsJson.Decode(payload));
            }
            if (kind != "event") throw new InvalidDataException("Bedrock ConverseStream 消息类型无效。"); var type = frame.StringHeader(":event-type"); if (type.Length == 0) throw new InvalidDataException("Bedrock ConverseStream 缺少事件名。"); var media = frame.StringHeader(":content-type", required: false); if (media.Length != 0 && !JsonMedia(media)) throw new NotSupportedException("Bedrock ConverseStream 未知二进制载荷不能隐式转换为 JSON，可读取原始帧。");
            if (type is "internalServerException" or "modelStreamErrorException" or "modelTimeoutException" or "serviceUnavailableException" or "throttlingException" or "validationException") throw new LlmUpstreamException("Bedrock ConverseStream 返回原生错误事件。", status, AwsJson.Decode(payload));
            var data = AwsJson.Parse(payload); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock ConverseStream 载荷必须是原生 JSON 对象。"); state.Observe(type, data); return AwsEventStreamExtensions.Attach(new BedrockConverseEvent(data, context.Metadata, type), frame);
        }
        catch (Exception error) { AwsEventStreamExtensions.Attach(error, frame); throw; }
    }
}
