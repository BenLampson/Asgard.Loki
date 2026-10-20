using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Video.Midjourney.Internal;

namespace Asgard.Loki.Video.Midjourney;

/// <summary>只向明确配置的非官方代理提交视频和查询一次，不托管代理或复用图片业务状态。</summary>
public sealed class MidjourneyVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _videoType;
    private readonly string? _secret;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获原生地址、视频类型、认证身份和本地策略；HTTP 工厂仍由应用拥有。</summary>
    public MidjourneyVideoAdapter(IHttpClientFactory factory, MidjourneyVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.VideoType, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); MidjourneyVideoJson.Text(text); }
        if (options.ApiSecret is { } secret)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secret); MidjourneyVideoJson.Text(secret);
            if (secret.Any(c => c is < '!' or > '~')) throw new ArgumentException("代理密钥不能包含空格、控制字符或非 ASCII 字符。", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("代理根地址必须是无凭据、查询、转义路径和片段的 HTTP/HTTPS 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _videoType = options.VideoType; _secret = options.ApiSecret;
        _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var credential = JsonSerializer.SerializeToUtf8Bytes(new[] { _secret });
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "midjourney-proxy-video-v1", Root = _root.AbsoluteUri, VideoType = _videoType, Operation = "submit/video+task/fetch",
            Authentication = "mj-api-secret-or-anonymous", Credential = Convert.ToHexString(SHA256.HashData(credential)), Client = _client,
            RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        CryptographicOperations.ZeroMemory(credential); BindingIdentity = "midjourney-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters.Any(p => string.Equals(p.Key, "videoType", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("原生 videoType 由完整目标固定，不能使用不同大小写覆盖。", nameof(request));
        snapshot.Parameters["videoType"] = _videoType; var body = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (body.Length > _requestLimit) throw new ArgumentException("代理视频原生请求超过显式字节上限。", nameof(request));
        return SendAsync(HttpMethod.Post, "submit/video", body, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); MidjourneyVideoJson.TaskId(taskId);
        return SendAsync(HttpMethod.Get, "task/" + Uri.EscapeDataString(taskId) + "/fetch", null, taskId, cancellationToken);
    }
    /// <summary>完成一个有界原生交换，先释放传输并检查取消，再交付原始协议窗口。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpMethod method, string path, byte[]? body, string? expectedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); HttpStatusCode status; byte[] bytes; string? contentType;
        using (var message = new HttpRequestMessage(method, new Uri(_root, path)))
        {
            if (_secret is not null) message.Headers.Add("mj-api-secret", _secret);
            message.Headers.Accept.ParseAdd("application/json");
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(_client); cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString();
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("代理视频 HTTP 调用失败。", status, bytes, contentType);
        var data = MidjourneyVideoJson.Parse(bytes, contentType); long? code = null;
        if (data.TryGetProperty("code", out var codeElement))
        {
            if (codeElement.ValueKind != JsonValueKind.Number || !codeElement.TryGetInt64(out var number)) throw new InvalidDataException("代理原生 code 必须是整数。");
            code = number;
            if (expectedId is null ? code is not (1 or 21 or 22) : code != 1) throw new VideoUpstreamException("代理返回原生 API 错误。", status, bytes, contentType);
        }
        else if (expectedId is null) throw new InvalidDataException("代理提交缺少原生 code。");
        var id = MidjourneyVideoJson.RequiredText(data, expectedId is null ? "result" : "id");
        try { MidjourneyVideoJson.TaskId(id); } catch (ArgumentException error) { throw new InvalidDataException("代理返回无效任务标识。", error); }
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("代理视频查询返回了其他任务的结果。");
        var native = data; var legacyVideoContext = false;
        if (expectedId is null)
        {
            native = default; legacyVideoContext = true;
            if (data.TryGetProperty("properties", out var properties) && properties.ValueKind != JsonValueKind.Null)
            {
                if (properties.ValueKind != JsonValueKind.Object) throw new InvalidDataException("代理提交 properties 必须是原生对象或 null。"); native = properties;
            }
        }
        else if (data.TryGetProperty("action", out _))
        {
            if (MidjourneyVideoJson.RequiredText(data, "action") != "VIDEO") throw new InvalidDataException("代理查询返回的不是视频任务。"); legacyVideoContext = true;
        }
        var hasStatus = native.ValueKind == JsonValueKind.Object && native.TryGetProperty("status", out _);
        if (!hasStatus && expectedId is not null) throw new InvalidDataException("代理查询缺少原生 status。");
        var nativeStatus = hasStatus ? MidjourneyVideoJson.RequiredText(native, "status") : null;
        var state = nativeStatus is null ? code == 22 ? VideoTaskState.Queued : VideoTaskState.Submitted : MidjourneyVideoProtocol.State(nativeStatus);
        if (state == VideoTaskState.Succeeded) MidjourneyVideoProtocol.ValidateVideoResult(native, legacyVideoContext);
        var transport = new VideoTaskHttpResponse(status, bytes, contentType); cancellationToken.ThrowIfCancellationRequested(); return new(id, nativeStatus, state, data, transport);
    }
}
