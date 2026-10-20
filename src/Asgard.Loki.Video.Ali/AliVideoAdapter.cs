using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.Ali.Internal;

namespace Asgard.Loki.Video.Ali;

/// <summary>万相原生视频提交与显式任务查询，不转换提示词、多模态输入或供应商任务状态原文。</summary>
public sealed class AliVideoAdapter : IVideoTaskAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string _model;
    private readonly string _key;
    private readonly string _client;
    private readonly string _submitPath;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获完整不可变上游；借用工厂，不发现凭据或按模型名称猜测原生接口。</summary>
    public AliVideoAdapter(IHttpClientFactory factory, AliVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); AliVideoHttp.Text(text); }
        if (options.ApiKey.Any(char.IsControl)) throw new ArgumentException("万相 API Key 不能包含控制字符。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("万相根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _submitPath = options.Operation switch
        {
            AliVideoOperation.VideoGeneration => "services/aigc/video-generation/video-synthesis",
            AliVideoOperation.ImageToVideo => "services/aigc/image2video/video-synthesis",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "万相原生视频操作无效。")
        };
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _model = options.Model; _key = options.ApiKey;
        _client = options.HttpClientName; Id = options.Id; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes;
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "dashscope-video-json-v1", Root = _root.AbsoluteUri, Model = _model, Operation = _submitPath, Client = _client,
            Credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key))), RequestBytes = _requestLimit, ResponseBytes = _responseLimit
        });
        BindingIdentity = "ali-video-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request.Snapshot();
        if (snapshot.Parameters["input"] is not JsonObject || snapshot.Parameters.ContainsKey("parameters") && snapshot.Parameters["parameters"] is not JsonObject)
            throw new ArgumentException("万相提交必须使用原生 input 对象及可选 parameters 对象。", nameof(request));
        snapshot.Parameters["model"] = _model; var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Parameters);
        if (bytes.Length > _requestLimit) throw new ArgumentException("万相提交正文超过显式字节上限。", nameof(request));
        var message = Message(HttpMethod.Post, _submitPath); message.Headers.Add("X-DashScope-Async", "enable");
        message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); return SendAsync(message, null, cancellationToken);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); AliVideoHttp.Text(taskId); cancellationToken.ThrowIfCancellationRequested();
        if (taskId is "." or "..") throw new ArgumentException("任务 ID 不能是路径跳转段。", nameof(taskId));
        return SendAsync(Message(HttpMethod.Get, "tasks/" + Uri.EscapeDataString(taskId)), taskId, cancellationToken);
    }
    /// <summary>在每个请求上固定认证，不修改借用工厂或客户端的默认头。</summary>
    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_root, path)); message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); return message;
    }
    /// <summary>保留完整原生任务结果；请求错误、处理失败和未知状态彼此独立。</summary>
    private async Task<VideoTaskSnapshot> SendAsync(HttpRequestMessage request, string? expectedId, CancellationToken cancellationToken)
    {
        var reply = await AliVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("万相 HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        var data = AliVideoHttp.Parse(reply.Body, reply.ContentType);
        if (data.TryGetProperty("code", out var code) && code.ValueKind != JsonValueKind.Null && (code.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(code.GetString())))
            throw new VideoUpstreamException("万相返回原生 API 错误。", reply.Status, reply.Body, reply.ContentType);
        if (!data.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object) throw new InvalidDataException("万相任务结果缺少原生 output 对象。");
        var id = AliVideoHttp.RequiredText(output, "task_id"); var status = AliVideoHttp.RequiredText(output, "task_status");
        if (id is "." or "..") throw new InvalidDataException("万相返回了不能用于查询的任务 ID。");
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("万相返回了其他任务的结果。");
        var state = status switch
        {
            "PENDING" => VideoTaskState.Queued, "RUNNING" => VideoTaskState.Running, "SUCCEEDED" => VideoTaskState.Succeeded,
            "FAILED" => VideoTaskState.Failed, "CANCELED" => VideoTaskState.Cancelled, _ => VideoTaskState.Unknown
        };
        if (state == VideoTaskState.Succeeded && !HasVideoUrl(output)) throw new InvalidDataException("万相成功任务未返回原生视频结果地址。");
        cancellationToken.ThrowIfCancellationRequested(); return new(id, status, state, data);
    }
    /// <summary>只检查已知原生结果地址位置，不下载、选择或改写完整原始结果。</summary>
    private static bool HasVideoUrl(JsonElement output)
        => TextUrl(output) || output.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object && TextUrl(results);
    /// <summary>地址必须是原始非空文本，不从其他字段拼接结果。</summary>
    private static bool TextUrl(JsonElement output) => output.TryGetProperty("video_url", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
}
