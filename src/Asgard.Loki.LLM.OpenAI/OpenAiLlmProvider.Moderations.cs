using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>原生 Moderations 使用独立请求/结果规则，沿用既有完整目标，不改变其他协议的传输行为。</summary>
public sealed partial class OpenAiLlmProvider
{
    /// <summary>执行单次原生完整 JSON，资源关闭和最终取消检查后才交付结果。</summary>
    async Task<Moderation> IModerationsAdapter.CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(context);
        var payload = OpenAiModerationPayload.Create(request, context, _httpOptions.MaximumModerationRequestBytes ?? 32 * 1024 * 1024);
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Moderations 根地址必须为无凭据、查询参数和片段的 HTTP(S) 绝对地址。");
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new NotSupportedException("OpenAI Moderations 需要目标显式 API Key 凭据。");
        var key = context.Credential.ApiKey;
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is < '!' or > '~')) throw new ArgumentException("原生认证凭据必须为无空白/控制字符的 ASCII 文本。");
        var endpoint = new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), "moderations"); byte[] bytes; System.Net.HttpStatusCode status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            message.Headers.Authorization = new("Bearer", key); message.Headers.Accept.ParseAdd("application/json");
            message.Content = new ByteArrayContent(payload.Bytes); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var client = _httpClientFactory.CreateClient(_httpClientName);
            foreach (var headerName in new[] { "Authorization", "api-key", "x-api-key" }) client.DefaultRequestHeaders.Remove(headerName);
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != endpoint)) throw new InvalidDataException("Moderations 实际响应目标或方法改变。");
            if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Moderations 不支持 SSE。");
            var length = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && length is null || length < 0) throw new InvalidDataException("Moderations 响应声明长度无效。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _httpOptions.MaximumBodyBytes, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; type = response.Content.Headers.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("OpenAI Moderations HTTP 调用失败。", status, bytes, type);
        JsonElement data;
        try { data = OpenAiHttpJson.Parse(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Moderations 响应不是有效 UTF-8。", error); }
        if (OpenAiHttpJson.IsError(data)) throw new LlmUpstreamException("OpenAI Moderations 返回原生业务错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            && !header.MediaType!.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Moderations 完整结果必须为 UTF-8 JSON。");
        OpenAiModerationResponse.Validate(data, payload.Count); cancellationToken.ThrowIfCancellationRequested(); return new(data, context.Metadata);
    }
}
