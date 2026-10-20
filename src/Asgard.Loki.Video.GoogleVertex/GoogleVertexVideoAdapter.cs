using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Video.GoogleVertex.Internal;

namespace Asgard.Loki.Video.GoogleVertex;

/// <summary>资源作用域内的原生 Vertex Veo 提交和显式查询，不登录、刷新令牌或下载云存储结果。</summary>
public sealed class GoogleVertexVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly IGoogleVertexVideoTokenSource _source;
    private readonly string _sourceIdentity;
    private readonly string _principal;
    private readonly string _resource;
    private readonly string _escapedResource;
    private readonly Uri _root;
    private readonly string _client;
    private readonly string? _quota;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    private readonly TimeProvider _time;
    /// <summary>捕获不可变完整目标；工厂、认证源与时钟均为借用对象，不受 SDK 释放。</summary>
    public GoogleVertexVideoAdapter(IHttpClientFactory factory, IGoogleVertexVideoTokenSource tokenSource, GoogleVertexVideoOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(tokenSource); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.PrincipalIdentity, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); VertexVideoJson.Text(text); }
        _escapedResource = VertexVideoJson.Resource(options.ModelResource); _resource = options.ModelResource;
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%')) throw new ArgumentException("Vertex 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        if (options.QuotaProjectId is { } quota) { VertexVideoJson.Segment(quota); _quota = quota; }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _sourceIdentity = tokenSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); VertexVideoJson.Text(_sourceIdentity);
        _factory = factory; _source = tokenSource; _principal = options.PrincipalIdentity; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/");
        _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; _time = timeProvider ?? TimeProvider.System; Id = options.Id;
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "vertex-veo-resource-lro-json-v1", Root = _root.AbsoluteUri, Resource = _resource, Principal = _principal, Source = _sourceIdentity,
            Client = _client, QuotaProject = _quota, RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        BindingIdentity = "vertex-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters["instances"] is not JsonArray { Count: > 0 } instances || instances.Any(value => value is not JsonObject)
            || snapshot.Parameters.ContainsKey("parameters") && snapshot.Parameters["parameters"] is not JsonObject)
            throw new ArgumentException("Vertex 视频提交需要原生 instances 对象数组及可选 parameters 对象。", nameof(request));
        return SendAsync(Encode(snapshot.Parameters), "predictLongRunning", null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); VertexVideoJson.OperationName(taskId, _resource);
        return SendAsync(Encode(new JsonObject { ["operationName"] = taskId }), "fetchPredictOperation", taskId, cancellationToken);
    }
    /// <summary>在认证等待前固定全部原生参数，不截断或改写大型媒体字段。</summary>
    private byte[] Encode(JsonObject body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > _requestLimit) throw new ArgumentException("Vertex 原生请求超过显式字节上限。"); return bytes;
    }
    /// <summary>每次操作取得一次同主体令牌；最终释放、来源身份与取消检查通过后才返回原生任务。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(byte[] body, string action, string? expectedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged();
        var access = await _source.GetTokenAsync(_principal, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Vertex 视频认证源未返回令牌。");
        cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged();
        if (access.PrincipalIdentity != _principal) throw new InvalidOperationException("Vertex 视频认证源返回其他主体。");
        if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex 视频认证源返回已过期令牌。");
        System.Net.HttpStatusCode status; byte[] bytes; string? contentType;
        using (var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, _escapedResource + ":" + action)))
        {
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken); if (_quota is not null) message.Headers.Add("x-goog-user-project", _quota);
            using var client = _factory.CreateClient(_client);
            cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged();
            if (client.DefaultRequestHeaders.Contains("x-goog-api-key") || client.DefaultRequestHeaders.Contains("api-key") || _quota is null && client.DefaultRequestHeaders.Contains("x-goog-user-project"))
                throw new InvalidOperationException("Vertex 视频默认 HTTP 头包含未绑定的认证或项目身份。");
            if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex 视频令牌在发送前已过期。");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString();
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged();
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("Vertex 视频 HTTP 调用失败。", status, bytes, contentType);
        var data = VertexVideoJson.Parse(bytes, contentType);
        if (!data.TryGetProperty("name", out _) && data.TryGetProperty("error", out _)) throw new VideoUpstreamException("Vertex 视频返回原生 API 错误。", status, bytes, contentType);
        var result = VertexVideoOperation.Parse(data, _resource, expectedName); cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged(); return result;
    }
    /// <summary>认证来源配置不能漂移，短期令牌值的同主体轮换不改变稳定目标身份。</summary>
    private void SourceUnchanged()
    {
        if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Vertex 视频认证源配置已改变，应注册新目标。");
    }
}
