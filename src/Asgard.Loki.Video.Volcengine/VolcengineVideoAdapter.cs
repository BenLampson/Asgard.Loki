using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Video.Volcengine.Internal;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>方舟原生视频提交与显式任务查询，保留原始状态、错误、素材引用和结果地址。</summary>
public sealed class VolcengineVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly string _model;
    private readonly string _key;
    private readonly string _client;
    private readonly Uri _root;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>保存完整不可变目标，借用工厂；调用者应关闭生成重试、hedging 和自动重定向。</summary>
    public VolcengineVideoAdapter(IHttpClientFactory factory, VolcengineVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); ArkVideoHttp.Text(value); }
        if (options.ApiKey.Any(char.IsControl)) throw new ArgumentException("方舟凭据不能包含控制字符。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https")
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("视频根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; Id = options.Id; _model = options.Model; _key = options.ApiKey; _client = options.HttpClientName;
        _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes;
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "ark-video-json-v1", Model = _model, Root = _root.AbsoluteUri, Client = _client,
            Credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key))), RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        BindingIdentity = "ark-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters["content"] is not System.Text.Json.Nodes.JsonArray content || content.Count == 0)
            throw new ArgumentException("方舟视频提交必须包含非空原生 content 数组。", nameof(request));
        var payload = snapshot.Parameters; payload["model"] = _model; var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > _requestLimit) throw new ArgumentException("方舟视频提交正文超过显式字节上限。", nameof(request));
        var message = Message(HttpMethod.Post, "contents/generations/tasks");
        message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json");
        return SendAsync(message, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ArkVideoHttp.Text(taskId); cancellationToken.ThrowIfCancellationRequested();
        if (taskId is "." or "..") throw new ArgumentException("任务 ID 不能是路径跳转段。", nameof(taskId));
        return SendAsync(Message(HttpMethod.Get, "contents/generations/tasks/" + Uri.EscapeDataString(taskId)), taskId, cancellationToken);
    }
    /// <summary>每次握手显式设置固定凭据，不修改借用工厂或客户端默认头。</summary>
    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_root, path)); message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); return message;
    }
    /// <summary>提交确认和任务失败分别建模，只有原生成功及结果共同出现才标记成功。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpRequestMessage request, string? expectedId, CancellationToken cancellationToken)
    {
        var reply = await ArkVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("方舟视频 HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        if (!ArkVideoHttp.IsJson(reply.ContentType))
            throw new InvalidDataException("方舟视频调用未返回原生 JSON。");
        var data = ArkVideoHttp.Parse(reply.Body);
        var hasStatus = data.TryGetProperty("status", out var statusValue) && statusValue.ValueKind != JsonValueKind.Null;
        if (data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && (!hasStatus || !data.TryGetProperty("id", out _)))
            throw new VideoUpstreamException("方舟视频返回原生 API 错误。", reply.Status, reply.Body, reply.ContentType);
        var id = ArkVideoHttp.RequiredText(data, "id");
        if (id is "." or "..") throw new InvalidDataException("方舟返回了不能用于查询的任务 ID。");
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("方舟返回了其他任务的结果。");
        var status = hasStatus ? ArkVideoHttp.RequiredText(data, "status") : null;
        if (expectedId is not null && status is null) throw new InvalidDataException("方舟任务查询缺少原生状态。");
        var state = status switch
        {
            null => VideoTaskState.Submitted, "queued" => VideoTaskState.Queued, "running" => VideoTaskState.Running,
            "succeeded" => VideoTaskState.Succeeded, "failed" => VideoTaskState.Failed, "cancelled" => VideoTaskState.Cancelled,
            "expired" => VideoTaskState.Expired, _ => VideoTaskState.Unknown
        };
        if (state == VideoTaskState.Succeeded && (!data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("video_url", out var video) || video.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(video.GetString())))
            throw new InvalidDataException("方舟成功任务未返回原生视频结果地址。");
        cancellationToken.ThrowIfCancellationRequested(); return new(id, status, state, data);
    }
}
