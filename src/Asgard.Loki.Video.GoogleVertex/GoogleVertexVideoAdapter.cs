using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.GoogleVertex.Internal;

namespace Asgard.Loki.Video.GoogleVertex;

/// <summary>资源作用域内的原生 Vertex Veo 提交和显式查询，不登录、刷新令牌或下载云存储结果。</summary>
public sealed class GoogleVertexVideoAdapter : IVideoTaskAdapter
{
    private readonly VertexVideoHttp _http;
    private readonly string _resource;
    private readonly string _escapedResource;
    private readonly Uri _root;
    private readonly int _requestLimit;
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
        if (options.QuotaProjectId is { } quota) VertexVideoJson.Segment(quota);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        var sourceIdentity = tokenSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity); VertexVideoJson.Text(sourceIdentity);
        _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/");
        _requestLimit = options.MaximumRequestBytes; Id = options.Id;
        _http = new(factory, tokenSource, sourceIdentity, options.PrincipalIdentity, options.HttpClientName,
            options.QuotaProjectId, options.MaximumResponseBytes, timeProvider ?? TimeProvider.System);
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "vertex-veo-resource-lro-json-v1", Root = _root.AbsoluteUri, Resource = _resource, Principal = options.PrincipalIdentity, Source = sourceIdentity,
            Client = options.HttpClientName, QuotaProject = options.QuotaProjectId, RequestBytes = _requestLimit, ResponseBytes = options.MaximumResponseBytes
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
        var (status, bytes, contentType) = await _http.SendAsync(HttpMethod.Post,
            new Uri(_root, _escapedResource + ":" + action), body, cancellationToken).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("Vertex 视频 HTTP 调用失败。", status, bytes, contentType);
        var data = VertexVideoJson.Parse(bytes, contentType);
        if (!data.TryGetProperty("name", out _) && data.TryGetProperty("error", out _)) throw new VideoUpstreamException("Vertex 视频返回原生 API 错误。", status, bytes, contentType);
        var result = VertexVideoOperation.Parse(data, _resource, expectedName); cancellationToken.ThrowIfCancellationRequested(); _http.EnsureSourceUnchanged(); return result;
    }
}
