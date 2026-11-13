using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.GoogleVertex.Internal;

namespace Asgard.Loki.Video.GoogleVertex;

/// <summary>通过原生 Vertex Interactions API 提交 Gemini Omni 视频任务及显式查询一次结果。</summary>
public sealed class GoogleVertexOmniVideoAdapter : IVideoTaskAdapter
{
    private readonly GoogleVertexOmniVideoOptions _options;
    private readonly Uri _endpoint;
    private readonly VertexVideoHttp _http;

    /// <summary>捕获实际模型、项目/区域地址及认证绑定；工厂、令牌源与时钟均为借用对象。</summary>
    public GoogleVertexOmniVideoAdapter(IHttpClientFactory factory, IGoogleVertexVideoTokenSource tokenSource,
        GoogleVertexOmniVideoOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tokenSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.InteractionsEndpoint);
        var endpoint = options.InteractionsEndpoint;
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath.Contains('%'))
            throw new ArgumentException("Vertex Interactions 地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        var parts = endpoint.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 6 || parts[^5] != "projects" || parts[^3] != "locations" || parts[^1] != "interactions")
            throw new ArgumentException("需要含版本和完整项目/区域作用域的 Interactions 集合地址。", nameof(options));
        foreach (var part in parts) VertexVideoJson.Segment(part);
        var sourceIdentity = tokenSource.BindingIdentity;
        foreach (var value in new[] { options.Id, options.PrincipalIdentity, options.HttpClientName, sourceIdentity })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            VertexVideoJson.Text(value);
        }
        VertexVideoJson.Segment(options.Model);
        if (options.QuotaProjectId is { } quota) VertexVideoJson.Segment(quota);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumRequestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumResponseBytes);

        _options = options;
        _endpoint = new(endpoint.AbsoluteUri.TrimEnd('/'));
        _http = new(factory, tokenSource, sourceIdentity, options.PrincipalIdentity, options.HttpClientName,
            options.QuotaProjectId, options.MaximumResponseBytes, timeProvider ?? TimeProvider.System);
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Endpoint = _endpoint.AbsoluteUri, options.Model, options.PrincipalIdentity, Source = sourceIdentity,
            options.HttpClientName, options.QuotaProjectId, options.MaximumRequestBytes, options.MaximumResponseBytes
        });
        BindingIdentity = "vertex-omni-interactions-video-v1:" + Convert.ToHexString(SHA256.HashData(identity));
    }

    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public string BindingIdentity { get; }

    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Snapshot().Parameters;
        if (Flag(body, "background") is not true || Flag(body, "store") is false || Flag(body, "stream") is true)
            throw new NotSupportedException("视频任务要求 background=true、可查询的存储结果及非流式 JSON。");
        if (body.ContainsKey("agent")) throw new ArgumentException("视频任务使用固定模型，不能同时选择 agent。", nameof(request));
        body["model"] = _options.Model;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > _options.MaximumRequestBytes) throw new ArgumentException("Vertex 视频请求超过显式字节上限。", nameof(request));
        return SendAsync(HttpMethod.Post, _endpoint, bytes, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VertexVideoJson.Segment(taskId);
        var uri = new Uri(_endpoint.AbsoluteUri + "/" + Uri.EscapeDataString(taskId));
        return SendAsync(HttpMethod.Get, uri, null, taskId, cancellationToken);
    }

    /// <summary>区分省略和明确的原生布尔值；禁止在任务入口覆盖调用方显式设置。</summary>
    private static bool? Flag(JsonObject body, string name)
    {
        if (!body.TryGetPropertyValue(name, out var node)) return null;
        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag : throw new ArgumentException($"原生 {name} 必须为布尔值。");
    }

    /// <summary>发送一次固定目标调用，完成所有资源检查后解析原生 Interactions 状态。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpMethod method, Uri uri, byte[]? body, string? expectedId, CancellationToken token)
    {
        var (status, bytes, type) = await _http.SendAsync(method, uri, body, token).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300)
            throw new VideoUpstreamException("Vertex Interactions 视频 HTTP 调用失败。", status, bytes, type);
        var data = VertexVideoJson.Parse(bytes, type);
        if (!data.TryGetProperty("id", out _) && data.TryGetProperty("error", out _))
            throw new VideoUpstreamException("Vertex Interactions 返回原生 API 错误。", status, bytes, type);
        var result = VertexOmniVideoInteraction.Parse(data, _options.Model, expectedId);
        token.ThrowIfCancellationRequested();
        _http.EnsureSourceUnchanged();
        return result;
    }
}
