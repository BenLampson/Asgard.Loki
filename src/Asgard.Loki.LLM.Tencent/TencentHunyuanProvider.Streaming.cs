using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Tencent.Internal;

namespace Asgard.Loki.LLM.Tencent;

/// <summary>混元原生 SSE 生命周期和尾部暂存，不借用 OpenAI 的 DONE 或 Choice delta 状态。</summary>
public sealed partial class TencentHunyuanProvider
{
    /// <summary>逐条保留平铺原生 JSON 与 SSE 元数据；首个选择结束后等待所有选择及资源正常关闭。</summary>
    public async IAsyncEnumerable<HunyuanEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var token = cancellationToken; token.ThrowIfCancellationRequested(); ValidateContext(context);
        var body = TencentPayload.Snapshot(request, context.Target.UpstreamModel, true, _options.MaximumRequestBytes); var credentials = await CredentialsAsync(context, token).ConfigureAwait(false);
        var state = new TencentStreamState(_options.MaximumTrackedChoices ?? 64); var pending = new List<(JsonElement Data, SseFrame Frame)>(); byte[]? rejected = null; string? type; HttpStatusCode status;
        using (var message = new HttpRequestMessage(HttpMethod.Post, context.Target.BaseAddress))
        using (var content = new ByteArrayContent(body))
        {
            message.Content = content; TencentWireGuard guard; HttpResponseMessage response;
            using (var client = _factory.CreateClient(_clientName))
            {
                SignMessage(client, message, body, credentials, context, true, token); guard = new(message, body);
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                using (response)
                {
                    AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); await guard.VerifyAsync(response, token).ConfigureAwait(false); type = RawContentType(response); status = response.StatusCode; CheckLength(response);
                    if (!response.IsSuccessStatusCode || !MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                        rejected = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, token).ConfigureAwait(false);
                    else
                    {
                        if (header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("混元 SSE 必须为 UTF-8。");
                        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        using var bounded = new TencentReadStream(source, _options.MaximumResponseBytes, response.Content.Headers.ContentLength, () => AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token));
                        await foreach (var frame in SseReader.ReadAsync(bounded, token, strictUtf8: true).ConfigureAwait(false))
                        {
                            AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); await guard.VerifyAsync(response, token).ConfigureAwait(false);
                            var bytes = Encoding.UTF8.GetBytes(frame.Data); var data = TencentJson.Parse(bytes);
                            if (TencentPayload.Error(data)) throw new LlmUpstreamException("腾讯混元 SSE 返回原生错误。", status, bytes, type);
                            state.Observe(data);
                            if (state.Finishing)
                            { if (pending.Count >= (_options.MaximumBufferedTerminalEvents ?? 128)) throw new InvalidDataException("混元 SSE 原生终态尾部事件数超限。"); pending.Add((data, frame)); }
                            else { AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); yield return new(data, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
                        }
                        state.Complete();
                    }
                    await guard.VerifyAsync(response, token).ConfigureAwait(false);
                }
            }
            await guard.VerifyAsync(response, token).ConfigureAwait(false);
        }
        AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token);
        if (rejected is not null)
        {
            if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("腾讯混元 SSE HTTP 调用失败。", status, rejected, type);
            if (TencentPayload.Error(TencentJson.Parse(rejected))) throw new LlmUpstreamException("腾讯混元流式调用返回原生错误。", status, rejected, type);
            throw new InvalidDataException("混元流式调用没有返回原生 SSE。");
        }
        foreach (var item in pending)
        { AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); yield return new(item.Data, context.Metadata, item.Frame.EventName, item.Frame.EventId, item.Frame.RetryMilliseconds); }
        AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token);
    }
}
