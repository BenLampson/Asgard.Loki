using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Video.Vidu.Internal;

namespace Asgard.Loki.Video.Vidu;

/// <summary>Vidu 原生视频固定目标，仅执行显式提交与单次任务查询，保留完整原生结果。</summary>
public sealed class ViduVideoAdapter : IVideoTaskAdapter
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
    public ViduVideoAdapter(IHttpClientFactory factory, ViduVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text); ViduVideoJson.Text(text);
        }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Vidu Token 不能包含空格、控制字符或非 ASCII 字符。", nameof(options));
        _path = options.Operation switch
        {
            ViduVideoOperation.TextToVideo => "ent/v2/text2video",
            ViduVideoOperation.ImageToVideo => "ent/v2/img2video",
            ViduVideoOperation.StartEndToVideo => "ent/v2/start-end2video",
            ViduVideoOperation.ReferenceToVideo => "ent/v2/reference2video",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Vidu 原生操作无效。")
        };
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("Vidu 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model; _key = options.ApiKey;
        _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var keyBytes = Encoding.UTF8.GetBytes(_key);
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "vidu-native-ent-v2-json-token", Root = _root.AbsoluteUri, Model = _model, Operation = _path,
            Credential = Convert.ToHexString(SHA256.HashData(keyBytes)), Client = _client, RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        CryptographicOperations.ZeroMemory(keyBytes);
        BindingIdentity = "vidu-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
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
        if (body.Length > _requestLimit) throw new ArgumentException("Vidu 原生请求超过显式字节上限。", nameof(request));
        return SendAsync(HttpMethod.Post, _path, body, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ViduVideoJson.TaskId(taskId);
        return SendAsync(HttpMethod.Get, "ent/v2/tasks/" + Uri.EscapeDataString(taskId) + "/creations", null, taskId, cancellationToken);
    }
    /// <summary>一个原生 HTTP 交换，读完并释放传输资源之后才解析和交付任务状态。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpMethod method, string path, byte[]? body, string? expectedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); HttpStatusCode status; byte[] bytes; string? contentType;
        using (var message = new HttpRequestMessage(method, new Uri(_root, path)))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Token", _key); message.Headers.Accept.ParseAdd("application/json");
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(_client); cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString();
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("Vidu 视频 HTTP 调用失败。", status, bytes, contentType);
        var data = ViduVideoJson.Parse(bytes, contentType);
        if (data.TryGetProperty("code", out var code))
        {
            if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var nativeCode)) throw new InvalidDataException("Vidu 原生错误 code 必须是整数。");
            if (nativeCode != 0) throw new VideoUpstreamException("Vidu 返回原生 API 错误。", status, bytes, contentType);
        }
        var id = ViduVideoJson.RequiredText(data, expectedId is null ? "task_id" : "id");
        try { ViduVideoJson.TaskId(id); } catch (ArgumentException error) { throw new InvalidDataException("Vidu 返回无效任务标识。", error); }
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("Vidu 查询返回了其他任务的结果。");
        var hasState = data.TryGetProperty("state", out _);
        if (!hasState && expectedId is not null) throw new InvalidDataException("Vidu 查询缺少原生 state。");
        var nativeState = hasState ? ViduVideoJson.RequiredText(data, "state") : null;
        var state = nativeState switch
        {
            null or "created" => VideoTaskState.Submitted, "queueing" => VideoTaskState.Queued, "processing" => VideoTaskState.Running,
            "success" => VideoTaskState.Succeeded, "failed" => VideoTaskState.Failed, _ => VideoTaskState.Unknown
        };
        if (state == VideoTaskState.Succeeded)
        {
            if (data.TryGetProperty("err_code", out var error) && (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString())))
                throw new InvalidDataException("Vidu 成功任务包含矛盾的原生错误。");
            if (!data.TryGetProperty("creations", out var creations) || creations.ValueKind != JsonValueKind.Array || creations.GetArrayLength() == 0)
                throw new InvalidDataException("Vidu 成功任务缺少原生 creations 列表。");
            foreach (var creation in creations.EnumerateArray())
            {
                if (creation.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vidu creation 必须是原生对象。");
                _ = ViduVideoJson.RequiredText(creation, "id"); _ = ViduVideoJson.RequiredText(creation, "url");
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(id, nativeState, state, data);
    }
}
