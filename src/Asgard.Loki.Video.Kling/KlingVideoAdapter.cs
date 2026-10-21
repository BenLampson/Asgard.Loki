using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.Video.Kling.Internal;

namespace Asgard.Loki.Video.Kling;

/// <summary>可灵原生文生/图生视频完整目标，只执行显式提交与对应服务的单次查询。</summary>
public sealed class KlingVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly TimeProvider _clock;
    private readonly Uri _root;
    private readonly string _model;
    private readonly string _path;
    private readonly KlingVideoAuthentication _authentication;
    private readonly string _apiKey;
    private readonly string _access;
    private readonly string _secret;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获完整不可变上游；借用 HTTP 工厂与可选时钟，不通过 Key 前缀猜测模式。</summary>
    public KlingVideoAdapter(IHttpClientFactory factory, KlingVideoOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.HttpClientName }) { ArgumentException.ThrowIfNullOrWhiteSpace(text); KlingVideoJson.Text(text); }
        if (!Enum.IsDefined(options.Authentication)) throw new ArgumentOutOfRangeException(nameof(options), "可灵原生认证方式无效。");
        if (options.Authentication == KlingVideoAuthentication.ApiKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); KlingVideoJson.Text(options.ApiKey);
            if (options.ApiKey.Any(c => c is < '!' or > '~') || !string.IsNullOrEmpty(options.AccessKeyId) || !string.IsNullOrEmpty(options.SecretAccessKey))
                throw new ArgumentException("API Key 模式需独立可用的原生凭据，不能混合 AK/SK。", nameof(options));
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessKeyId); ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretAccessKey);
            KlingVideoJson.Text(options.AccessKeyId); KlingVideoJson.Text(options.SecretAccessKey);
            if (!string.IsNullOrEmpty(options.ApiKey) || options.AccessKeyId.Any(char.IsControl) || options.SecretAccessKey.Any(char.IsControl))
                throw new ArgumentException("JWT 模式需独立 AK/SK，不能混合 API Key 或控制字符。", nameof(options));
        }
        _path = options.Operation switch
        {
            KlingVideoOperation.TextToVideo => "v1/videos/text2video", KlingVideoOperation.ImageToVideo => "v1/videos/image2video",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "可灵原生生成操作无效。")
        };
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("可灵根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _clock = timeProvider ?? TimeProvider.System; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model;
        _authentication = options.Authentication; _apiKey = _authentication == KlingVideoAuthentication.ApiKey ? options.ApiKey : "";
        _access = _authentication == KlingVideoAuthentication.AccessKeyJwt ? options.AccessKeyId : ""; _secret = _authentication == KlingVideoAuthentication.AccessKeyJwt ? options.SecretAccessKey : "";
        _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var credentials = JsonSerializer.SerializeToUtf8Bytes(new[] { _apiKey, _access, _secret });
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "kling-native-v1-json-jwt30m-nbf5s", Root = _root.AbsoluteUri, Model = _model, Operation = _path, Authentication = _authentication,
            Credential = Convert.ToHexString(SHA256.HashData(credentials)), Client = _client, RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        CryptographicOperations.ZeroMemory(credentials); BindingIdentity = "kling-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters.ContainsKey("model_name")) throw new ArgumentException("原生 model_name 由完整目标固定，不能在正文参数中覆盖。", nameof(request));
        snapshot.Parameters["model_name"] = _model; var body = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (body.Length > _requestLimit) throw new ArgumentException("可灵原生提交超过显式字节上限。", nameof(request));
        return SendAsync(HttpMethod.Post, _path, body, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); KlingVideoJson.TaskId(taskId);
        return SendAsync(HttpMethod.Get, _path + "/" + Uri.EscapeDataString(taskId), null, taskId, cancellationToken);
    }
    /// <summary>每次显式调用产生一次原生认证，等待创建客户端后再次验证 JWT 时效，不重签重试。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpMethod method, string path, byte[]? body, string? expectedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var access = _authentication == KlingVideoAuthentication.AccessKeyJwt ? KlingVideoJwt.Create(_access, _secret, _clock.GetUtcNow().ToUnixTimeSeconds()) : (_apiKey, long.MinValue, long.MaxValue);
        HttpStatusCode status; byte[] bytes; string? contentType;
        using (var message = new HttpRequestMessage(method, new Uri(_root, path)))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Item1); message.Headers.Accept.ParseAdd("application/json");
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(_client); cancellationToken.ThrowIfCancellationRequested();
            if (_authentication == KlingVideoAuthentication.AccessKeyJwt)
            {
                var now = _clock.GetUtcNow().ToUnixTimeSeconds();
                if (now < access.Item2 || now >= access.Item3) throw new InvalidOperationException("可灵 JWT 在发送前不处于有效时间范围。");
            }
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString(); bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new VideoUpstreamException("可灵视频 HTTP 调用失败。", status, bytes, contentType);
        var data = KlingVideoJson.Parse(bytes, contentType);
        if (!data.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var nativeCode)) throw new InvalidDataException("可灵结果缺少原生整数 code。");
        if (nativeCode != 0) throw new VideoUpstreamException("可灵返回原生 API 错误。", status, bytes, contentType);
        if (!data.TryGetProperty("data", out var task) || task.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可灵成功信封缺少原生 data 对象。");
        var id = KlingVideoJson.RequiredText(task, "task_id");
        try { KlingVideoJson.TaskId(id); } catch (ArgumentException error) { throw new InvalidDataException("可灵返回无效任务标识。", error); }
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("可灵查询返回了其他任务的结果。");
        var hasStatus = task.TryGetProperty("task_status", out _);
        if (!hasStatus && expectedId is not null) throw new InvalidDataException("可灵查询缺少原生任务状态。");
        var nativeStatus = hasStatus ? KlingVideoJson.RequiredText(task, "task_status") : null;
        var state = nativeStatus switch
        {
            null or "submitted" => VideoTaskState.Submitted, "processing" => VideoTaskState.Running, "succeed" => VideoTaskState.Succeeded,
            "failed" => VideoTaskState.Failed, _ => VideoTaskState.Unknown
        };
        if (state == VideoTaskState.Succeeded)
        {
            if (!task.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("videos", out var videos)
                || videos.ValueKind != JsonValueKind.Array || videos.GetArrayLength() == 0) throw new InvalidDataException("可灵成功任务缺少原生视频列表。");
            foreach (var video in videos.EnumerateArray())
            {
                if (video.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可灵视频结果必须是原生对象。"); _ = KlingVideoJson.RequiredText(video, "url");
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(id, nativeStatus, state, data);
    }
}
