using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Image.Midjourney.Internal;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>明确代理上的原生图片调用客户端，只执行调用者指定的单次操作，不维护账户或任务后台。</summary>
public sealed class MidjourneyImageClient
{
    private readonly MidjourneyImageHttp _http;
    private readonly string _fingerprint;
    /// <summary>绑定原生根地址、独立认证身份、适配实现及客户端策略，借用 HTTP 工厂。</summary>
    public MidjourneyImageClient(IHttpClientFactory factory, MidjourneyImageOptions options)
    {
        _http = new(factory, options);
        var credential = JsonSerializer.SerializeToUtf8Bytes(new[] { options.ApiSecret });
        try
        {
            var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { "midjourney-proxy-image-v1", options.Id, options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/",
                Convert.ToHexString(SHA256.HashData(credential)), options.HttpClientName, options.MaxRequestBytes.ToString(CultureInfo.InvariantCulture), options.MaxResponseBytes.ToString(CultureInfo.InvariantCulture) });
            _fingerprint = Convert.ToHexString(SHA256.HashData(identity));
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }
    /// <summary>本固定目标标识。</summary>
    public string Id => _http.Options.Id;
    /// <summary>调用者明确关联已有原生任务 ID，不查询存在性、不赋予权限、不创建本地任务号。</summary>
    public MidjourneyImageTaskReference ReferenceTask(string taskId)
    { MidjourneyImageJson.TaskId(taskId); return new(Id, _fingerprint, taskId); }
    /// <summary>按明确的原生操作提交一次完整 JSON，原样保留父任务、按钮、图像索引、mask 和未知字段。</summary>
    public Task<MidjourneyImageSubmission> SubmitAsync(MidjourneyImageOperation operation, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var path = operation switch
        {
            MidjourneyImageOperation.Imagine => "submit/imagine", MidjourneyImageOperation.Describe => "submit/describe",
            MidjourneyImageOperation.Blend => "submit/blend", MidjourneyImageOperation.Change => "submit/change",
            MidjourneyImageOperation.SimpleChange => "submit/simple-change", MidjourneyImageOperation.Action => "submit/action",
            MidjourneyImageOperation.Modal => "submit/modal", MidjourneyImageOperation.Shorten => "submit/shorten",
            MidjourneyImageOperation.Edits => "submit/edits", MidjourneyImageOperation.SwapFace => "insight-face/swap",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return SubmitCoreAsync(path, MidjourneyImageJson.Request(parameters, _http.Options.MaxRequestBytes), cancellationToken);
    }
    private async Task<MidjourneyImageSubmission> SubmitCoreAsync(string path, byte[] body, CancellationToken token)
    {
        var response = await _http.SendAsync(HttpMethod.Post, path, body, token).ConfigureAwait(false); Code(response, true);
        var id = MidjourneyImageJson.RequiredText(response.Data, "result");
        MidjourneyImageTaskReference reference;
        try { reference = ReferenceTask(id); } catch (ArgumentException error) { throw new InvalidDataException("图片代理返回无效任务 ID。", error); }
        var result = new MidjourneyImageSubmission(response.Data, response.HttpResponse, reference); token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原生 JSON 图像输入上传一次，保留 result URL 数组，不将其绑定为任务或自动提交绘图。</summary>
    public Task<MidjourneyImageResponse> UploadInputsAsync(JsonObject parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var body = MidjourneyImageJson.Request(parameters, _http.Options.MaxRequestBytes);
        return UploadCoreAsync(body, cancellationToken);
    }
    private async Task<MidjourneyImageResponse> UploadCoreAsync(byte[] body, CancellationToken token)
    {
        var response = await _http.SendAsync(HttpMethod.Post, "submit/upload-discord-images", body, token).ConfigureAwait(false); Code(response, false);
        if (!response.Data.TryGetProperty("result", out var urls) || urls.ValueKind != JsonValueKind.Array
            || urls.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))) throw new InvalidDataException("图片代理输入上传需要原生 URL 字符串数组。");
        token.ThrowIfCancellationRequested(); return response;
    }
    /// <summary>只查询与本完整目标关联的任务一次，不轮询、推算完成或获取媒体。</summary>
    public Task<MidjourneyImageTask> GetAsync(MidjourneyImageTaskReference reference, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Validate(reference); return GetCoreAsync(reference, cancellationToken); }
    private async Task<MidjourneyImageTask> GetCoreAsync(MidjourneyImageTaskReference reference, CancellationToken token)
    {
        var response = await _http.SendAsync(HttpMethod.Get, "task/" + Uri.EscapeDataString(reference.TaskId) + "/fetch", null, token).ConfigureAwait(false);
        if (response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("code", out _)) Code(response, false);
        var task = Task(response.Data, response.HttpResponse, reference); token.ThrowIfCancellationRequested(); return task;
    }
    /// <summary>显式请求一次原生 seed；代理可能排队执行 seed 工作，不把返回值猜测成已完成 seed 或新任务。</summary>
    public Task<MidjourneyImageResponse> RequestImageSeedAsync(MidjourneyImageTaskReference reference, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Validate(reference); return SeedCoreAsync(reference, cancellationToken); }
    private async Task<MidjourneyImageResponse> SeedCoreAsync(MidjourneyImageTaskReference reference, CancellationToken token)
    {
        var response = await _http.SendAsync(HttpMethod.Get, "task/" + Uri.EscapeDataString(reference.TaskId) + "/image-seed", null, token).ConfigureAwait(false);
        Code(response, true); token.ThrowIfCancellationRequested(); return response;
    }
    /// <summary>按明确绑定的任务 ID 查询一次；保留原生返回顺序，允许缺项，不使用数组下标关联。</summary>
    public Task<MidjourneyImageTaskList> GetManyAsync(IReadOnlyList<MidjourneyImageTaskReference> references, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(references);
        var snapshot = references.ToArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in snapshot) { Validate(reference); if (!ids.Add(reference.TaskId)) throw new ArgumentException("批量图片任务 ID 不能重复。"); }
        var body = parameters?.DeepClone().AsObject() ?? new JsonObject();
        if (body.Any(p => p.Key.Equals("ids", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("批量 ids 由绑定引用生成，不能覆盖。");
        body["ids"] = new JsonArray(snapshot.Select(r => (JsonNode?)JsonValue.Create(r.TaskId)).ToArray());
        return ManyCoreAsync(snapshot, MidjourneyImageJson.Request(body, _http.Options.MaxRequestBytes), cancellationToken);
    }
    private async Task<MidjourneyImageTaskList> ManyCoreAsync(MidjourneyImageTaskReference[] references, byte[] body, CancellationToken token)
    {
        var response = await _http.SendAsync(HttpMethod.Post, "task/list-by-condition", body, token).ConfigureAwait(false);
        if (response.Data.ValueKind == JsonValueKind.Object) { Code(response, false); throw new InvalidDataException("图片任务批量查询必须返回原生数组。"); }
        var byId = references.ToDictionary(r => r.TaskId, StringComparer.Ordinal); var returned = new HashSet<string>(StringComparer.Ordinal); var tasks = new List<MidjourneyImageTask>();
        foreach (var item in response.Data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("图片批量任务项必须为原生对象。");
            var id = MidjourneyImageJson.RequiredText(item, "id");
            if (!returned.Add(id) || !byId.TryGetValue(id, out var reference)) throw new InvalidDataException("图片批量查询包含重复或未请求的任务 ID。");
            tasks.Add(Task(item, response.HttpResponse, reference));
        }
        token.ThrowIfCancellationRequested(); return new(response.Data, response.HttpResponse, tasks);
    }
    private void Validate(MidjourneyImageTaskReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference); MidjourneyImageJson.TaskId(reference.TaskId);
        if (reference.TargetId != Id || reference.TargetFingerprint != _fingerprint) throw new ArgumentException("图片任务与完整代理目标不符。", nameof(reference));
    }
    private static MidjourneyImageTask Task(JsonElement data, MidjourneyImageHttpResponse http, MidjourneyImageTaskReference reference)
    {
        if (data.ValueKind != JsonValueKind.Object || MidjourneyImageJson.RequiredText(data, "id") != reference.TaskId) throw new InvalidDataException("图片查询返回了其他任务。");
        return new(data, http, reference);
    }
    private static void Code(MidjourneyImageResponse response, bool taskCodes)
    {
        if (response.Data.ValueKind != JsonValueKind.Object || !response.Data.TryGetProperty("code", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var code))
            throw new InvalidDataException("图片代理结果缺少原生整数 code。");
        if (taskCodes ? code is not (1 or 21 or 22) : code != 1)
            throw new ImageUpstreamException("图片代理返回原生 API 错误。", response.HttpResponse.StatusCode, response.HttpResponse.Body, response.HttpResponse.ContentType);
    }
}
