using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.Google.Internal;

namespace Asgard.Loki.Video.Google;

/// <summary>Gemini Developer API 原生视频提交、单次查询和显式内容传输，独立于 Vertex。</summary>
public sealed class GoogleVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _model;
    private readonly string _key;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    private readonly GoogleVideoContent _content;
    /// <summary>捕获完整目标；借用工厂，调用者必须为命名客户端关闭自动重定向及自动重试。</summary>
    public GoogleVideoAdapter(IHttpClientFactory factory, GoogleVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); GoogleVideoHttp.Text(text); }
        GoogleVideoHttp.Segment(options.Model);
        if (options.ApiKey.Any(char.IsControl)) throw new ArgumentException("API Key 不能包含控制字符。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("Gemini 原生根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumContentBytes, 0); ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumContentRedirects);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumContentRedirects, 10);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model; _key = options.ApiKey;
        _client = options.HttpClientName; Id = options.Id; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes;
        _content = new(factory, _client, _root, _key, options.MaximumContentBytes, _responseLimit, options.MaximumContentRedirects);
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "gemini-video-lro-json-v1", Root = _root.AbsoluteUri, Model = _model, Client = _client,
            Credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key))), RequestBytes = _requestLimit, ResponseBytes = _responseLimit,
            ContentBytes = options.MaximumContentBytes, Redirects = options.MaximumContentRedirects
        });
        BindingIdentity = "google-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
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
            throw new ArgumentException("Gemini 视频提交需要原生 instances 对象数组及可选 parameters 对象。", nameof(request));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (bytes.Length > _requestLimit) throw new ArgumentException("Gemini 视频提交超过显式字节上限。", nameof(request));
        var message = Message(HttpMethod.Post, "models/" + Uri.EscapeDataString(_model) + ":predictLongRunning");
        message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); return SendAsync(message, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); GoogleVideoHttp.Text(taskId); cancellationToken.ThrowIfCancellationRequested();
        GoogleVideoOperation.Name(taskId, _model);
        return SendAsync(Message(HttpMethod.Get, string.Join('/', taskId.Split('/').Select(Uri.EscapeDataString))), taskId, cancellationToken);
    }
    /// <summary>显式复制调用者选中的原生文件 URI；只对当前服务根下的 files/{id}:download 首次请求发送凭据，不创建文件管理或后台下载流程。</summary>
    public Task<long> CopyContentToAsync(Uri videoUri, Stream destination, CancellationToken cancellationToken = default)
        => _content.CopyAsync(videoUri, destination, cancellationToken);
    /// <summary>原生任务认证按请求设置，不修改共享客户端的默认头。</summary>
    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_root, path)); message.Headers.Add("x-goog-api-key", _key); message.Headers.Accept.ParseAdd("application/json"); return message;
    }
    /// <summary>HTTP/API 错误与已命名操作的原生失败分别处理，保留完整结果。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpRequestMessage request, string? expectedName, CancellationToken cancellationToken)
    {
        var reply = await GoogleVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("Gemini 视频 HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        var data = GoogleVideoHttp.Parse(reply.Body, reply.ContentType);
        if (!data.TryGetProperty("name", out _) && data.TryGetProperty("error", out _))
            throw new VideoUpstreamException("Gemini 视频返回原生 API 错误。", reply.Status, reply.Body, reply.ContentType);
        var result = GoogleVideoOperation.Parse(data, _model, expectedName); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
