using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>Azure 自己的原生文本补全传输，不套用 Chat 请求和结果结构。</summary>
public sealed partial class AzureLlmProvider
{
    /// <inheritdoc />
    async Task<TextCompletion> ITextCompletionsAdapter.CompleteAsync(JsonObject payload, LlmAdapterContext context, CancellationToken cancellationToken)
    {
        byte[] bytes; HttpStatusCode status; string? type; DateTimeOffset? expiry = null;
        using (var request = await PrepareTextAsync(payload, context, false, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            var expected = request.RequestUri; BeforeSend(client, request, context, cancellationToken); CheckTextDefaults(client);
            if (request.Options.TryGetValue(TokenExpiry, out var acquired)) expiry = acquired;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); CheckTextResponse(request, expected, response); cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true) throw new InvalidDataException("完整文本补全不接受 SSE。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumBodyBytes, cancellationToken).ConfigureAwait(false); status = response.StatusCode; type = TextContentType(response);
        }
        cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry);
        var data = ParseTextBody(bytes, status, type); AzureTextCompletionJson.Complete(data); var result = new TextCompletion(data, context.Metadata);
        cancellationToken.ThrowIfCancellationRequested(); ValidateTextIdentity(context, expiry); return result;
    }
    /// <summary>认证等待前冻结原生请求，保留显式版本/部署路径并只选择本次凭据。</summary>
    private async Task<HttpRequestMessage> PrepareTextAsync(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Azure 文本补全不支持所选认证方式。"); var target = context.Target; var root = target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("文本补全根必须是无凭据、查询和片段的 HTTP(S) 地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); AzureJson.Text(target.UpstreamModel); var dated = _options.ApiStyle == AzureLlmApiStyle.Dated;
        if (dated && target.UpstreamModel is "." or "..") throw new ArgumentException("部署名不能是相对路径段。");
        var bytes = AzureTextCompletionJson.Request(payload, context, stream, dated, _options.MaximumTextCompletionRequestBytes ?? 32 * 1024 * 1024);
        var path = dated ? "deployments/" + Uri.EscapeDataString(target.UpstreamModel) + "/completions" : "completions"; if (_options.ApiVersion is not null) path += "?api-version=" + Uri.EscapeDataString(_options.ApiVersion);
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; message.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            if (context.Credential.Kind == LlmCredentialKind.ApiKey)
            { var key = context.Credential.ApiKey; if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is < '!' or > '~')) throw new ArgumentException("Azure API Key 必须为无空白/控制字符的 ASCII 文本。"); message.Headers.Add("api-key", key); }
            else
            {
                SourceUnchanged(); var access = await _tokenSource!.GetTokenAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Azure 认证源没有返回令牌。"); token.ThrowIfCancellationRequested(); SourceUnchanged();
                if (access.PrincipalIdentity != context.Credential.PrincipalIdentity || access.ExpiresOn <= _time.GetUtcNow() || access.AccessToken.Any(c => c is < '!' or > '~')) throw new InvalidOperationException("Azure 文本补全令牌主体、期限或格式无效。");
                message.Headers.Authorization = new("Bearer", access.AccessToken); message.Options.Set(TokenExpiry, access.ExpiresOn);
            }
            token.ThrowIfCancellationRequested(); return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>外部主体和令牌期限必须在交付边界仍有效，不重新取令牌。</summary>
    private void ValidateTextIdentity(LlmAdapterContext context, DateTimeOffset? expiry)
    { if (context.Credential.Kind == LlmCredentialKind.External) { SourceUnchanged(); if (expiry is null || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 文本补全令牌在结果交付前已过期。"); } }
    /// <summary>拒绝非 Azure 的未选认证默认头，不修改借用工厂。</summary>
    private static void CheckTextDefaults(HttpClient client) { if (client.DefaultRequestHeaders.Contains("x-api-key")) throw new InvalidOperationException("Azure 客户端包含未选认证方式的默认头。"); }
    /// <summary>错误媒体类型即使不可解析也保留原始头值。</summary>
    private static string? TextContentType(HttpResponseMessage response) => response.Content.Headers.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
    /// <summary>固定发送前的实际目标，并在读取正文前拒绝无效长度声明。</summary>
    private void CheckTextResponse(HttpRequestMessage request, Uri? expected, HttpResponseMessage response)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri != expected || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != expected)) throw new InvalidDataException("文本补全实际目标或方法改变。");
        var length = response.Content.Headers.ContentLength; if (response.Content.Headers.Contains("Content-Length") && length is null || length < 0 || length > _options.MaximumBodyBytes) throw new InvalidDataException("文本补全声明长度无效或超限。");
    }
    /// <summary>先保留原生/HTTP 错误，再检查成功 JSON 类型与严格编码。</summary>
    private static JsonElement ParseTextBody(byte[] bytes, HttpStatusCode status, string? type)
    {
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Azure 文本补全 HTTP 调用失败。", status, bytes, type);
        JsonElement data; try { data = AzureJson.Parse(bytes); } catch (DecoderFallbackException error) { throw new InvalidDataException("Azure 文本补全不是有效 UTF-8。", error); }
        if (AzureTextCompletionJson.Error(data)) throw new LlmUpstreamException("Azure 返回原生文本补全错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) && !header.MediaType!.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || !TextCharset(header))) throw new InvalidDataException("Azure 文本补全必须是完整 UTF-8 JSON。"); return data;
    }
    /// <summary>字符集只能省略或明确 UTF-8，不做有损转码。</summary>
    private static bool TextCharset(MediaTypeHeaderValue header) => header.CharSet is not { } charset || charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase);
}
