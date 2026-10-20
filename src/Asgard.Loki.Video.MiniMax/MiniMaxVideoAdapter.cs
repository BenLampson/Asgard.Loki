using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.MiniMax.Internal;

namespace Asgard.Loki.Video.MiniMax;

/// <summary>MiniMax 原生视频 v1/v2 完整目标，显式提交、查询及 v1 成功视频结果元数据调用。</summary>
public sealed class MiniMaxVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _model;
    private readonly string _key;
    private readonly string _client;
    private readonly MiniMaxVideoApiVersion _version;
    private readonly string _versionPath;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获完整不可变配置，借用工厂；原生版本不按实际模型名称推断。</summary>
    public MiniMaxVideoAdapter(IHttpClientFactory factory, MiniMaxVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); MiniMaxVideoHttp.Text(text); }
        if (options.ApiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ArgumentException("MiniMax API Key 不能包含空白或控制字符。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%')) throw new ArgumentException("MiniMax 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        if (!Enum.IsDefined(options.ApiVersion)) throw new ArgumentOutOfRangeException(nameof(options), "MiniMax 原生版本无效。");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model; _key = options.ApiKey; _client = options.HttpClientName;
        _version = options.ApiVersion; _versionPath = _version == MiniMaxVideoApiVersion.V1 ? "v1" : "v2"; Id = options.Id; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes;
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "minimax-video-native-json-v1", Root = _root.AbsoluteUri, Model = _model, Version = _versionPath, Client = _client,
            Credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key))), RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        BindingIdentity = "minimax-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (_version == MiniMaxVideoApiVersion.V2 && (snapshot.Parameters["content"] is not JsonArray { Count: > 0 } content || content.Any(value => value is not JsonObject)))
            throw new ArgumentException("MiniMax v2 提交需要原生 content 对象数组。", nameof(request));
        snapshot.Parameters["model"] = _model; var body = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (body.Length > _requestLimit) throw new ArgumentException("MiniMax 原生提交超过显式字节上限。", nameof(request));
        var message = Message(HttpMethod.Post, _versionPath + "/video_generation"); message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json");
        return SubmitCoreAsync(message, cancellationToken);
    }
    /// <summary>保留原生提交确认，不能把尚无状态的任务合成为成功。</summary>
    private async Task<VideoTaskSnapshot> SubmitCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var data = await SendAsync(request, _version == MiniMaxVideoApiVersion.V1, cancellationToken).ConfigureAwait(false);
        var result = MiniMaxVideoProtocol.Submit(data); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        MiniMaxVideoHttp.TaskId(taskId); cancellationToken.ThrowIfCancellationRequested(); var escaped = Uri.EscapeDataString(taskId);
        var path = _version == MiniMaxVideoApiVersion.V1 ? "v1/query/video_generation?task_id=" + escaped : "v2/query/video_generation/" + escaped;
        return QueryCoreAsync(Message(HttpMethod.Get, path), taskId, cancellationToken);
    }
    /// <summary>显式取得一次任务窗口，不补文件查询、下载或后台轮询。</summary>
    private async Task<VideoTaskSnapshot> QueryCoreAsync(HttpRequestMessage request, string expectedId, CancellationToken cancellationToken)
    {
        var data = await SendAsync(request, _version == MiniMaxVideoApiVersion.V1, cancellationToken).ConfigureAwait(false);
        var result = MiniMaxVideoProtocol.Query(data, _version, expectedId); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>仅为成功 v1 视频快照取得一次原生结果文件元数据，不接受任意文件目录操作，也不下载签名地址。</summary>
    public Task<MiniMaxVideoResultFile> GetResultFileAsync(VideoTaskSnapshot completedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedTask); cancellationToken.ThrowIfCancellationRequested();
        if (_version != MiniMaxVideoApiVersion.V1) throw new NotSupportedException("MiniMax v2 结果已在原生 task.content 中，不使用 v1 文件结果调用。");
        if (completedTask.State != VideoTaskState.Succeeded || MiniMaxVideoProtocol.HasApiError(completedTask.Data, true)
            || MiniMaxVideoProtocol.Query(completedTask.Data, MiniMaxVideoApiVersion.V1, completedTask.TaskId).State != VideoTaskState.Succeeded)
            throw new ArgumentException("结果文件调用只接受成功 v1 视频任务的原生快照。", nameof(completedTask));
        var fileId = MiniMaxVideoProtocol.FileId(completedTask.Data); return ResultFileCoreAsync(fileId, cancellationToken);
    }
    /// <summary>结果文件 ID 必须对应原生视频任务；用途、签名地址和元数据完整保留。</summary>
    private async Task<MiniMaxVideoResultFile> ResultFileCoreAsync(string fileId, CancellationToken cancellationToken)
    {
        var data = await SendAsync(Message(HttpMethod.Get, "v1/files/retrieve?file_id=" + fileId), true, cancellationToken).ConfigureAwait(false);
        if (!data.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object || MiniMaxVideoProtocol.FileId(file) != fileId)
            throw new InvalidDataException("MiniMax 视频结果元数据缺少对应的原生文件。");
        if (MiniMaxVideoHttp.RequiredText(file, "purpose") != "video_generation") throw new InvalidDataException("MiniMax 返回了非视频生成用途的文件。");
        var url = MiniMaxVideoHttp.RequiredText(file, "download_url"); cancellationToken.ThrowIfCancellationRequested(); return new(fileId, url, data);
    }
    /// <summary>认证只设置在显式原生请求上，不根据返回 URL 转发凭据。</summary>
    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_root, path)); message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); message.Headers.Accept.ParseAdd("application/json"); return message;
    }
    /// <summary>区分 HTTP/API 错误与任务失败，全部资源释放后才解析和公开完整原文。</summary>
    private async Task<JsonElement> SendAsync(HttpRequestMessage request, bool requireBase, CancellationToken cancellationToken)
    {
        var reply = await MiniMaxVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("MiniMax 视频 HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        var data = MiniMaxVideoHttp.Parse(reply.Body, reply.ContentType);
        if (MiniMaxVideoProtocol.HasApiError(data, requireBase)) throw new VideoUpstreamException("MiniMax 视频返回原生 API 错误。", reply.Status, reply.Body, reply.ContentType);
        cancellationToken.ThrowIfCancellationRequested(); return data;
    }
}
