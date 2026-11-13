using System.Net;
using System.Net.Http.Headers;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Video.GoogleVertex.Internal;

/// <summary>借用同一 Vertex 认证源的单次 JSON 传输，不解释 Veo 或 Interactions 状态。</summary>
internal sealed class VertexVideoHttp(
    IHttpClientFactory factory, IGoogleVertexVideoTokenSource source, string sourceIdentity,
    string principal, string clientName, string? quota, int responseLimit, TimeProvider clock)
{
    /// <summary>认证、完整正文和外层资源均确认后返回原始 HTTP 窗口。</summary>
    internal async Task<(HttpStatusCode Status, byte[] Body, string? ContentType)> SendAsync(
        HttpMethod method, Uri uri, byte[]? body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSourceUnchanged();
        var access = await source.GetTokenAsync(principal, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vertex 视频认证源未返回令牌。");
        token.ThrowIfCancellationRequested();
        EnsureSourceUnchanged();
        if (access.PrincipalIdentity != principal) throw new InvalidOperationException("Vertex 视频认证源返回其他主体。");
        if (access.ExpiresOn <= clock.GetUtcNow()) throw new InvalidOperationException("Vertex 视频认证源返回已过期令牌。");

        HttpStatusCode status;
        byte[] bytes;
        string? contentType;
        using (var message = new HttpRequestMessage(method, uri))
        {
            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.ContentType = new("application/json");
            }
            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
            if (quota is not null) message.Headers.Add("x-goog-user-project", quota);
            using var client = factory.CreateClient(clientName);
            token.ThrowIfCancellationRequested();
            EnsureSourceUnchanged();
            if (client.DefaultRequestHeaders.Contains("x-goog-api-key") || client.DefaultRequestHeaders.Contains("api-key")
                || client.DefaultRequestHeaders.Contains("x-api-key") || quota is null && client.DefaultRequestHeaders.Contains("x-goog-user-project"))
                throw new InvalidOperationException("Vertex 视频默认 HTTP 头包含未绑定的认证或项目身份。");
            if (access.ExpiresOn <= clock.GetUtcNow()) throw new InvalidOperationException("Vertex 视频令牌在发送前已过期。");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (message.Method != method || message.RequestUri != uri
                || response.RequestMessage is { } actual && (actual.Method != method || actual.RequestUri != uri))
                throw new InvalidDataException("Vertex 视频实际 HTTP 方法或目标发生改变。");
            status = response.StatusCode;
            contentType = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
            if (response.Content.Headers.NonValidated.Contains("Content-Length") && response.Content.Headers.ContentLength is null)
                throw new InvalidDataException("Vertex 视频响应的 Content-Length 无效。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, responseLimit, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        EnsureSourceUnchanged();
        return (status, bytes, contentType);
    }

    /// <summary>同一稳定主体的令牌可轮换，认证来源配置不能漂移。</summary>
    internal void EnsureSourceUnchanged()
    {
        if (source.BindingIdentity != sourceIdentity) throw new InvalidOperationException("Vertex 视频认证源配置已改变，应注册新目标。");
    }
}
