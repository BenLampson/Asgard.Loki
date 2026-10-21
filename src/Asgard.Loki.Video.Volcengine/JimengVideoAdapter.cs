using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.Volcengine.Internal;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>即梦 Visual 原生视频提交与单次查询，固定完整 req_key 和独立 AK/SK 签名。</summary>
public sealed class JimengVideoAdapter : IVideoTaskAdapter
{
    private const string Version = "2022-08-31";
    private const string Region = "cn-north-1";
    private const string Service = "cv";
    private readonly IHttpClientFactory _factory;
    private readonly TimeProvider _clock;
    private readonly Uri _root;
    private readonly string _requestKey;
    private readonly string _accessKey;
    private readonly string _secret;
    private readonly string? _token;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获不可变完整目标；借用 HTTP 工厂与签名时钟，不发现凭据或迁移模型。</summary>
    public JimengVideoAdapter(IHttpClientFactory factory, JimengVideoOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.RequestKey, options.AccessKeyId, options.SecretAccessKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); VolcengineVideoHttp.Text(text); }
        if (!options.AccessKeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || options.SecretAccessKey.Any(char.IsControl)
            || options.SecurityToken is { } token && (token.Length == 0 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new ArgumentException("Visual 签名凭据格式无效。", nameof(options));
        if (options.SecurityToken is { } securityToken) VolcengineVideoHttp.Text(securityToken);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.AbsolutePath != "/"
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("Visual 地址必须是无凭据、查询和片段的 HTTPS 根地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _clock = timeProvider ?? TimeProvider.System; _root = options.BaseAddress; _requestKey = options.RequestKey;
        _accessKey = options.AccessKeyId; _secret = options.SecretAccessKey; _token = options.SecurityToken; _client = options.HttpClientName;
        _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var credentials = JsonSerializer.SerializeToUtf8Bytes(new[] { _accessKey, _secret, _token });
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "jimeng-visual-cv-video-json-v1", Root = _root.AbsoluteUri, RequestKey = _requestKey, Region, Service, Version, Client = _client,
            Credential = Convert.ToHexString(SHA256.HashData(credentials)), RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        CryptographicOperations.ZeroMemory(credentials); BindingIdentity = "jimeng-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters.ContainsKey("req_key")) throw new ArgumentException("原生 req_key 由完整目标固定，不能在提交参数中覆盖。", nameof(request));
        snapshot.Parameters["req_key"] = _requestKey; return Invoke(snapshot.Parameters, "CVSync2AsyncSubmitTask", null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default) => GetAsync(taskId, new JsonObject(), cancellationToken);
    /// <summary>以原生附加查询字段显式查询一次；保留 req_json 字符串，不解析或重写水印配置。</summary>
    public Task<VideoTaskSnapshot> GetAsync(string taskId, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); VolcengineVideoHttp.Text(taskId); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = VolcengineVideoHttp.Snapshot(parameters);
        if (snapshot.ContainsKey("req_key") || snapshot.ContainsKey("task_id")) throw new ArgumentException("查询参数不能覆盖完整目标 req_key 或本次 task_id。", nameof(parameters));
        if (snapshot.TryGetPropertyValue("req_json", out var configuration) && (configuration is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
            throw new ArgumentException("Visual req_json 必须保持原生 JSON 字符串。", nameof(parameters));
        snapshot["req_key"] = _requestKey; snapshot["task_id"] = taskId; return Invoke(snapshot, "CVSync2AsyncGetResult", taskId, cancellationToken);
    }
    /// <summary>签名和发送的是同一份完整 UTF-8 正文；原生 Action、版本、区域和服务固定。</summary>
    private Task<VideoTaskSnapshot> Invoke(JsonObject body, string action, string? expectedId, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > _requestLimit) throw new ArgumentException("Visual 原生请求超过显式字节上限。", nameof(body));
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, "?Action=" + action + "&Version=" + Version));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.ParseAdd("application/json");
            VolcengineRequestSigner.Sign(message, bytes, _accessKey, _secret, _token, Region, Service, _clock.GetUtcNow());
        }
        catch { message.Dispose(); throw; }
        return SendAsync(message, expectedId, cancellationToken);
    }
    /// <summary>先判断原生外层 code，再读取 data；不把 done 或 HTTP 200 单独当作成功。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpRequestMessage request, string? expectedId, CancellationToken cancellationToken)
    {
        var reply = await VolcengineVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("即梦 Visual HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        if (!VolcengineVideoHttp.IsJson(reply.ContentType)) throw new InvalidDataException("Visual 没有返回原生 JSON。");
        var data = VolcengineVideoHttp.Parse(reply.Body);
        if (data.TryGetProperty("ResponseMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("Error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new VideoUpstreamException("Visual 返回原生 OpenAPI 错误。", reply.Status, reply.Body, reply.ContentType);
        if (!data.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value)) throw new InvalidDataException("Visual 结果缺少原生整数 code。");
        if (value != 10000) throw new VideoUpstreamException("Visual 返回原生非成功 code。", reply.Status, reply.Body, reply.ContentType);
        if (!data.TryGetProperty("data", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Visual 成功信封缺少原生 data 对象。");
        if (result.TryGetProperty("req_key", out _) && VolcengineVideoHttp.RequiredText(result, "req_key") != _requestKey) throw new InvalidDataException("Visual 返回了其他 req_key 的结果。");
        VideoTaskSnapshot snapshot;
        if (expectedId is null)
        {
            var id = VolcengineVideoHttp.RequiredText(result, "task_id"); snapshot = new(id, null, VideoTaskState.Submitted, data);
        }
        else
        {
            if (result.TryGetProperty("task_id", out _) && VolcengineVideoHttp.RequiredText(result, "task_id") != expectedId) throw new InvalidDataException("Visual 返回了其他任务的结果。");
            var status = VolcengineVideoHttp.RequiredText(result, "status");
            var state = status switch
            {
                "in_queue" => VideoTaskState.Queued, "generating" => VideoTaskState.Running, "done" => VideoTaskState.Succeeded,
                "expired" => VideoTaskState.Expired, _ => VideoTaskState.Unknown
            };
            if (state == VideoTaskState.Succeeded) _ = VolcengineVideoHttp.RequiredText(result, "video_url");
            // 原生查询通常不回传 task_id；仅以本次显式请求作为关联上下文，不向 Data 补造原生字段。
            snapshot = new(expectedId, status, state, data);
        }
        cancellationToken.ThrowIfCancellationRequested(); return snapshot;
    }
}
