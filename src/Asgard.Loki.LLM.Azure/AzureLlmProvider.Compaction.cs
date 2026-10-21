using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>原生压缩窗口和 Azure 认证独立处理，不改写其他协议传输或重建历史。</summary>
public sealed partial class AzureLlmProvider
{
    /// <inheritdoc />
    public async Task<ResponseCompaction> CompactAsync(JsonObject payload, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        byte[] bytes; HttpStatusCode status; string? type; DateTimeOffset? expiry = null;
        using (var request = await PrepareCompactionAsync(payload, context, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            var expectedUri = request.RequestUri;
            BeforeSend(client, request, context, cancellationToken);
            if (client.DefaultRequestHeaders.Contains("x-api-key")) throw new InvalidOperationException("Azure 客户端包含未选认证方式的默认头。");
            if (request.Options.TryGetValue(TokenExpiry, out var acquiredExpiry)) expiry = acquiredExpiry;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (request.Method != HttpMethod.Post || request.RequestUri != expectedUri || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != expectedUri)) throw new InvalidDataException("Azure 压缩响应的实际方法或目标改变。");
            if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("原生压缩不支持 SSE。");
            var length = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && length is null || length < 0) throw new InvalidDataException("Azure 压缩响应声明长度无效。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumBodyBytes, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; type = response.Content.Headers.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Credential.Kind == LlmCredentialKind.External)
        {
            SourceUnchanged(); if (expiry is null || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 压缩令牌在结果交付前已过期。");
        }
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Azure 原生压缩 HTTP 调用失败。", status, bytes, type);
        JsonElement result;
        try { result = AzureJson.Parse(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Azure 压缩响应不是有效 UTF-8。", error); }
        if (AzureJson.IsError(result)) throw new LlmUpstreamException("Azure 返回原生压缩错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            && !header.MediaType!.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Azure 原生压缩只接受完整 UTF-8 JSON。");
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 压缩结果必须是原生 JSON 对象。");
        var window = new ResponseCompaction(result, context.Metadata);
        if (context.Credential.Kind == LlmCredentialKind.External)
        { SourceUnchanged(); if (expiry is null || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 令牌在压缩结果交付前已过期。"); }
        cancellationToken.ThrowIfCancellationRequested(); return window;
    }
    /// <summary>首次认证等待前冻结全部请求，只选择明确配置的根/版本和实际部署，不注入 stream。</summary>
    private async Task<HttpRequestMessage> PrepareCompactionAsync(JsonObject payload, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Azure 压缩未声明支持所选认证方式。");
        var target = context.Target; ArgumentNullException.ThrowIfNull(target); var root = target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Azure 压缩根地址必须为无凭据、查询和片段的 HTTP(S) 绝对地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); AzureJson.Text(target.UpstreamModel); AzureJson.Input(payload);
        if (payload.Any(p => p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) && p.Key != "model"))
            throw new ArgumentException("原生压缩不接受 stream 或模型字段别名。");
        var body = payload.DeepClone().AsObject(); body["model"] = target.UpstreamModel;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > (_options.MaximumCompactionRequestBytes ?? 32 * 1024 * 1024)) throw new ArgumentException("Azure 压缩请求超过本地 UTF-8 字节上限。");
        var raw = AzureJson.Parse(bytes);
        if (raw.TryGetProperty("input", out var input) && input.ValueKind is not (JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("原生压缩 input 必须为字符串、原生项数组或空值。");
        if (raw.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null
            && (previous.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(previous.GetString()))) throw new ArgumentException("原生响应引用必须为非空字符串或空值。");
        var path = "responses/compact";
        if (_options.ApiVersion is not null) path += "?api-version=" + Uri.EscapeDataString(_options.ApiVersion);
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; message.Headers.Accept.ParseAdd("application/json");
            if (context.Credential.Kind == LlmCredentialKind.ApiKey)
            {
                var key = context.Credential.ApiKey;
                if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is < '!' or > '~')) throw new ArgumentException("Azure API Key 必须为无空白/控制字符的 ASCII 文本。");
                message.Headers.Add("api-key", key);
            }
            else
            {
                SourceUnchanged();
                var access = await _tokenSource!.GetTokenAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Azure 认证源未返回令牌。");
                token.ThrowIfCancellationRequested(); SourceUnchanged();
                if (access.PrincipalIdentity != context.Credential.PrincipalIdentity || access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 令牌主体不匹配或已过期。");
                if (access.AccessToken.Any(c => c is < '!' or > '~')) throw new InvalidOperationException("Azure 认证源返回了无效 Bearer 凭据。");
                message.Headers.Authorization = new("Bearer", access.AccessToken); message.Options.Set(TokenExpiry, access.ExpiresOn);
            }
            token.ThrowIfCancellationRequested(); return message;
        }
        catch { message.Dispose(); throw; }
    }
}
