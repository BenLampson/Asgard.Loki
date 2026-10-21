using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Google.Internal;

namespace Asgard.Loki.LLM.Google;

/// <summary>完整有界 JSON 和原始错误在资源释放、最后取消之后交付。</summary>
public sealed partial class GoogleLlmProvider
{
    private async Task<JsonElement> SendAsync(JsonObject payload, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[] bytes; HttpStatusCode status; string? type; HttpResponseMessage? actual = null; GoogleConfiguredHttpCall? configured;
        using (var request = Request(payload, context, false, out configured))
        using (var client = _factory.CreateClient(_clientName))
        {
            token.ThrowIfCancellationRequested(); if (configured is null) ValidateDefaults(client); else configured.BeforeSend(client);
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            { actual = response; configured?.Verify(response); status = response.StatusCode; type = ContentType(response.Content); _ = Length(response.Content); bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumBodyBytes, token).ConfigureAwait(false); }
        }
        token.ThrowIfCancellationRequested(); configured?.Verify(actual); if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Gemini 原生 HTTP 调用失败。", status, bytes, type);
        var data = GoogleHttpJson.Parse(bytes); token.ThrowIfCancellationRequested(); return data;
    }
    /// <summary>完整保留实际媒体头；不把二进制错误转换成有损字符串。</summary>
    private static string? ContentType(HttpContent content) => content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
    /// <summary>无效或超预算的声明不能被未知长度当作正常响应继续读取。</summary>
    private long? Length(HttpContent content)
    {
        long? length; try { length = content.Headers.ContentLength; } catch (FormatException error) { throw new InvalidDataException("Gemini 原始长度声明无效。", error); }
        if (content.Headers.NonValidated.Contains("Content-Length") && length is null || length < 0 || length > _options.MaximumBodyBytes) throw new InvalidDataException("Gemini 原始长度声明无效或超过预算。"); return length;
    }
}
