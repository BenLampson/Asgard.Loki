using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Video.XAI.Internal;

namespace Asgard.Loki.Video.XAI;

/// <summary>Xai 原生视频固定目标，仅执行显式提交与单次任务查询，保留完整原生结果。</summary>
public sealed class XaiVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _model;
    private readonly string _path;
    private readonly string _key;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;

    /// <summary>捕获原生操作、模型、服务器及凭据身份，借用工厂而不接管其生命周期。</summary>
    public XaiVideoAdapter(IHttpClientFactory factory, XaiVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text); XaiVideoJson.Text(text);
        }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Xai Bearer 不能包含空格、控制字符或非 ASCII 字符。", nameof(options));
        _path = options.Operation switch
        {
            XaiVideoOperation.Generate => "v1/videos/generations",
            XaiVideoOperation.Edit => "v1/videos/edits",
            XaiVideoOperation.Extend => "v1/videos/extensions",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Xai 原生操作无效。")
        };
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("Xai 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model; _key = options.ApiKey;
        _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var keyBytes = Encoding.UTF8.GetBytes(_key);
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "xai-native-v1-json-http202", Root = _root.AbsoluteUri, Model = _model, Operation = _path,
            Credential = Convert.ToHexString(SHA256.HashData(keyBytes)), Client = _client, RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        CryptographicOperations.ZeroMemory(keyBytes);
        BindingIdentity = "xai-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.Snapshot(); snapshot.Parameters["model"] = _model;
        var body = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (body.Length > _requestLimit) throw new ArgumentException("Xai 原生请求超过显式字节上限。", nameof(request));
        return SendAsync(HttpMethod.Post, _path, body, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); XaiVideoJson.TaskId(taskId);
        return SendAsync(HttpMethod.Get, "v1/videos/" + Uri.EscapeDataString(taskId), null, taskId, cancellationToken);
    }
    /// <summary>一个原生 HTTP 交换，读完并释放传输资源之后才解析和交付任务状态。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpMethod method, string path, byte[]? body, string? expectedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); HttpStatusCode status; byte[] bytes; string? contentType;
        using (var message = new HttpRequestMessage(method, new Uri(_root, path)))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); message.Headers.Accept.ParseAdd("application/json");
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(_client); cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString();
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("Xai 视频 HTTP 调用失败。", status, bytes, contentType);
        var transport = new VideoTaskHttpResponse(status, bytes, contentType);
        if (expectedId is not null && status == HttpStatusCode.Accepted && bytes.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); return new(expectedId, null, VideoTaskState.Running, default, transport);
        }
        var data = XaiVideoJson.Parse(bytes, contentType);
        var hasStatus = data.TryGetProperty("status", out _);
        var hasError = data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
        if (hasError && (!hasStatus || expectedId is null && !data.TryGetProperty("request_id", out _)))
            throw new VideoUpstreamException("xAI 返回原生 API 错误。", status, bytes, contentType);
        string id;
        if (expectedId is null)
        {
            id = XaiVideoJson.RequiredText(data, "request_id");
            try { XaiVideoJson.TaskId(id); } catch (ArgumentException failure) { throw new InvalidDataException("xAI 返回无效请求标识。", failure); }
        }
        else
        {
            id = expectedId;
            if (data.TryGetProperty("request_id", out _) && XaiVideoJson.RequiredText(data, "request_id") != expectedId)
                throw new InvalidDataException("xAI 查询返回了其他请求的结果。");
            if (!hasStatus) throw new InvalidDataException("xAI 查询缺少原生 status。");
        }
        var nativeStatus = hasStatus ? XaiVideoJson.RequiredText(data, "status") : null;
        var state = nativeStatus switch
        {
            null => VideoTaskState.Submitted, "pending" => VideoTaskState.Running, "done" => VideoTaskState.Succeeded,
            "failed" => VideoTaskState.Failed, "expired" => VideoTaskState.Expired, _ => VideoTaskState.Unknown
        };
        if (nativeStatus == "done")
        {
            if (!data.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object) throw new InvalidDataException("xAI done 结果缺少原生 video 对象。");
            if (video.TryGetProperty("respect_moderation", out var moderation))
            {
                if (moderation.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("xAI 原生审核标记必须是布尔值。");
                if (!moderation.GetBoolean()) state = VideoTaskState.Failed;
            }
            if (state == VideoTaskState.Succeeded)
            {
                if (hasError) throw new InvalidDataException("xAI 成功结果包含矛盾的原生错误。");
                _ = XaiVideoJson.RequiredText(video, "url");
            }
        }
        if (nativeStatus == "pending" && hasError) throw new InvalidDataException("xAI pending 结果包含矛盾的原生错误。");
        if (status == HttpStatusCode.Accepted && state is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Expired)
            throw new InvalidDataException("xAI HTTP 202 与原生终态矛盾。");
        cancellationToken.ThrowIfCancellationRequested(); return new(id, nativeStatus, state, data, transport);
    }
}
