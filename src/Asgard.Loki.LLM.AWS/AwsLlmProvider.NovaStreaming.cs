using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Nova Invoke 二进制 chunk 中保留完整原生 JSON 信封，不复用 Claude 或 Converse 报文形状。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>原生结束和用量尾帧通过 EOF、长度、释放及最终取消后交付；不合成完成。</summary>
    public async IAsyncEnumerable<BedrockNovaEvent> NovaStreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, context, true, cancellationToken, nova: true).ConfigureAwait(false); var state = new AwsNovaStreamState(_options.MaximumTrackedNovaBlocks ?? 128); List<BedrockNovaEvent> pending = [];
        using (var message = prepared.Request)
        using (var client = _factory.CreateClient(_clientName))
        {
            AuthenticateBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
                throw new LlmUpstreamException("Bedrock Nova 流式调用失败。", response.StatusCode, AwsJson.Decode(bytes));
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/vnd.amazon.eventstream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await AwsJson.ReadAsync(source, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false); var data = AwsJson.Parse(bytes);
                if (IsStreamError(data)) throw new LlmUpstreamException("Bedrock Nova 流式调用返回平台错误。", response.StatusCode, AwsJson.Decode(bytes)); throw new InvalidDataException("Bedrock Nova 流式调用未返回二进制 EventStream。");
            }
            if (response.Headers.TryGetValues("x-amzn-bedrock-content-type", out var contentTypes))
            {
                var values = contentTypes.ToArray(); if (values.Length != 1 || !JsonMedia(values[0])) throw new InvalidDataException("Bedrock Nova 流内模型内容不是 JSON。");
            }
            await foreach (var frame in AwsEventStreamReader.ReadAsync(source, _options.MaximumEventStreamFrameBytes, _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken, _options.MaximumEventStreamHeaders).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); var item = DecodeNovaEvent(frame, state, context, response.StatusCode);
                if (state.Finishing) { if (pending.Count >= _options.MaximumBufferedTerminalEvents) throw AwsEventStreamExtensions.Attach(new InvalidDataException("Bedrock Nova 终态尾部暂存数超限。"), frame); pending.Add(item); }
                else yield return item;
            }
            state.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested(); foreach (var item in pending) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }
    /// <summary>外层帧及 Base64 包装通过附加视图保留，结果 Data 始终是完整内层信封。</summary>
    private static BedrockNovaEvent DecodeNovaEvent(AwsEventStreamFrame frame, AwsNovaStreamState state, LlmAdapterContext context, System.Net.HttpStatusCode status)
    {
        try
        {
            var kind = frame.StringHeader(":message-type"); var payload = frame.Payload.ToArray();
            if (kind is "exception" or "error")
            {
                if (kind == "exception") _ = frame.StringHeader(":exception-type"); else { _ = frame.StringHeader(":error-code"); _ = frame.StringHeader(":error-message"); }
                throw new LlmUpstreamException("Bedrock Nova EventStream 返回传输错误。", status, AwsJson.Decode(payload));
            }
            if (kind != "event") throw new InvalidDataException("Bedrock Nova EventStream 消息类型无效。"); var eventType = frame.StringHeader(":event-type"); if (eventType.Length == 0) throw new InvalidDataException("Bedrock Nova EventStream 缺少事件名。");
            if (eventType is "internalServerException" or "modelStreamErrorException" or "modelTimeoutException" or "serviceUnavailableException" or "throttlingException" or "validationException") throw new LlmUpstreamException("Bedrock Nova EventStream 返回原生服务错误。", status, AwsJson.Decode(payload));
            var media = frame.StringHeader(":content-type", required: false); if (media.Length != 0 && !JsonMedia(media)) throw new NotSupportedException("Bedrock Nova 未知二进制载荷不能隐式转为 JSON，可读取原始帧。");
            var data = AwsJson.Parse(payload); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock Nova 帧载荷必须是原生 JSON 对象。"); string? nativeType = null;
            if (eventType == "chunk")
            {
                if (!data.TryGetProperty("bytes", out var blob) || blob.ValueKind != JsonValueKind.String) throw new InvalidDataException("Bedrock Nova chunk 缺少 Base64 bytes。");
                var decoded = blob.GetBytesFromBase64(); data = AwsJson.Parse(decoded); if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock Nova chunk 必须包含原生 JSON 信封。");
                foreach (var error in new[] { "internalServerException", "modelStreamErrorException", "modelTimeoutException", "serviceUnavailableException", "throttlingException", "validationException" })
                    if (data.TryGetProperty(error, out _)) throw new LlmUpstreamException("Bedrock Nova 信封包含原生服务错误。", status, AwsJson.Decode(decoded));
                var known = new[] { "messageStart", "contentBlockStart", "contentBlockDelta", "contentBlockStop", "messageStop", "metadata" }.Where(name => data.TryGetProperty(name, out _)).ToArray();
                if (known.Length > 1) throw new InvalidDataException("Bedrock Nova 信封包含多个互斥生命周期事件。");
                if (known.Length == 1) { nativeType = known[0]; var body = data.GetProperty(nativeType); if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock Nova 原生事件必须是对象。"); state.Observe(nativeType, body); }
            }
            return AwsEventStreamExtensions.Attach(new BedrockNovaEvent(data, context.Metadata, eventType, nativeType), frame);
        }
        catch (Exception error) { AwsEventStreamExtensions.Attach(error, frame); throw; }
    }
}
