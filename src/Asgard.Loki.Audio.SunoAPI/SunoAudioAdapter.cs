using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Audio.SunoAPI.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.SunoAPI;

/// <summary>原生 MUSIC/LYRICS 显式任务调用；不执行账号管理、轮询或媒体下载。</summary>
public sealed class SunoAudioAdapter : IAudioBatchTaskAdapter, IAudioTaskResultAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    private readonly string? _musicModel;
    private readonly string _key;
    private readonly SunoAudioAuthentication _authentication;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>固定完整目标与身份，不读取账号配置或创建网络连接。</summary>
    public SunoAudioAdapter(IHttpClientFactory factory, SunoAudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); SunoAudioJson.Text(text); }
        if (options.MusicModel is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(options.MusicModel); SunoAudioJson.Text(options.MusicModel); }
        if (!Enum.IsDefined(options.Authentication)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.ApiKey);
        if (options.Authentication == SunoAudioAuthentication.Bearer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
            if (options.ApiKey.Any(value => value is < '!' or > '~')) throw new ArgumentException("代理凭据必须为无空格和控制字符的 ASCII 文本。");
        }
        else if (options.ApiKey.Length != 0) throw new ArgumentException("匿名代理不能同时配置凭据。");
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("Suno-API 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"); _musicModel = options.MusicModel;
        _key = options.ApiKey; _authentication = options.Authentication; _client = options.HttpClientName;
        _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes; Id = options.Id;
        var secretBytes = Encoding.UTF8.GetBytes(_key); string credential;
        try { credential = Convert.ToHexString(SHA256.HashData(secretBytes)); }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
        var identity = JsonSerializer.Serialize(new
        {
            Implementation = "suno-api-native-music-lyrics-json-v1", Root = _root.AbsoluteUri, MusicModel = _musicModel,
            Authentication = _authentication, Credential = credential, Client = _client, RequestLimit = _requestLimit, ResponseLimit = _responseLimit
        });
        BindingIdentity = "suno-audio-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity { get; }
    /// <inheritdoc />
    public Task<AudioTaskSnapshot> SubmitAsync(AudioTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var copy = request.Snapshot(); SunoAudioJson.Operation(copy.Operation);
        var known = new[] { "mv", "prompt", "title", "tags", "gpt_description_prompt", "task_id", "continue_at", "continue_clip_id", "make_instrumental" };
        foreach (var member in copy.Parameters)
        {
            var exact = known.FirstOrDefault(name => name.Equals(member.Key, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && exact != member.Key) throw new ArgumentException("Suno-API 已知字段不能使用大小写别名。");
            if (exact is "prompt" or "title" or "tags" or "gpt_description_prompt" or "task_id" or "continue_clip_id")
            {
                if (member.Value?.GetValueKind() != JsonValueKind.String) throw new ArgumentException("Suno-API 原生文本字段必须保持字符串。");
            }
            else if (exact == "make_instrumental" && member.Value?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Suno-API make_instrumental 必须为原生布尔值。");
            else if (exact == "continue_at")
            {
                if (member.Value?.GetValueKind() != JsonValueKind.Number || !double.TryParse(member.Value.ToJsonString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) || !double.IsFinite(at) || at < 0)
                    throw new ArgumentException("Suno-API continue_at 必须为非负有限数字。");
            }
        }
        if (copy.Operation == "MUSIC")
        {
            if (_musicModel is null) throw new InvalidOperationException("音乐提交必须显式配置原生 mv。");
            if (copy.Parameters.TryGetPropertyValue("mv", out var model))
            {
                if (model?.GetValueKind() != JsonValueKind.String || model.GetValue<string>() != _musicModel)
                    throw new ArgumentException("Suno-API 请求 mv 与完整目标不一致。");
            }
            else copy.Parameters["mv"] = _musicModel;
        }
        var body = Encode(copy.Parameters); return SubmitCoreAsync(copy.Operation, body, cancellationToken);
    }
    /// <summary>只接受原生字符串 success 确认；没有状态时返回已提交而不伪造原生状态。</summary>
    private async Task<AudioTaskSnapshot> SubmitCoreAsync(string operation, byte[] body, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Post, "suno/submit/" + operation, body, cancellationToken).ConfigureAwait(false);
        var id = SunoAudioJson.Required(root, "data");
        try { SunoAudioJson.TaskId(id); } catch (ArgumentException error) { throw new InvalidDataException("Suno-API 提交返回无效任务 ID。", error); }
        cancellationToken.ThrowIfCancellationRequested(); return new(id, operation, null, AudioTaskState.Submitted, root);
    }
    /// <inheritdoc />
    public async Task<AudioTaskSnapshot> GetAsync(AudioTaskQuery query, CancellationToken cancellationToken = default)
    {
        var result = await GetBatchAsync([query], cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Tasks.Count == 1 ? result.Tasks[0] : throw new SunoAudioTaskMissingException(query.TaskId, result.Data);
    }
    /// <inheritdoc />
    public Task<AudioTaskBatchSnapshot> GetBatchAsync(IReadOnlyList<AudioTaskQuery> queries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries); cancellationToken.ThrowIfCancellationRequested();
        var copy = queries.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(copy.Length);
        foreach (var query in copy) Validate(query);
        if (copy.Select(query => query.TaskId).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("Suno-API 批量任务 ID 重复。");
        var body = Encode(new { ids = copy.Select(query => query.TaskId).ToArray() });
        return BatchCoreAsync(copy, body, cancellationToken);
    }
    /// <summary>只关联实际返回记录，完整信封保留原生顺序；缺失任务不补造。</summary>
    private async Task<AudioTaskBatchSnapshot> BatchCoreAsync(AudioTaskQuery[] queries, byte[] body, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Post, "suno/fetch", body, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Suno-API 批量状态必须返回原生数组。");
        var expected = queries.ToDictionary(query => query.TaskId, StringComparer.Ordinal); var found = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<AudioTaskSnapshot>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Suno-API 任务记录必须是原生对象。");
            var id = SunoAudioJson.Required(item, "task_id");
            if (!expected.TryGetValue(id, out var query) || !found.Add(id)) throw new InvalidDataException("Suno-API 批量返回其他任务或重复任务。");
            tasks.Add(SunoAudioJson.Window(item, query));
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(tasks, root);
    }
    /// <inheritdoc />
    public Task<JsonElement> GetResultAsync(AudioTaskQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Validate(query); return ResultCoreAsync(query, cancellationToken);
    }
    /// <summary>作者协议仅返回任务数据；兼容代理回显任务对象时检查关联但保持整个对象不变。</summary>
    private async Task<JsonElement> ResultCoreAsync(AudioTaskQuery query, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Get, "suno/fetch/" + Uri.EscapeDataString(query.TaskId), null, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty("data", out var data)) throw new InvalidDataException("Suno-API 结果接口缺少原生 data。");
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("task_id", out _))
        {
            if (SunoAudioJson.Required(data, "task_id") != query.TaskId
                || data.TryGetProperty("action", out _) && SunoAudioJson.Required(data, "action") != query.Operation)
                throw new InvalidDataException("Suno-API 结果接口回显其他任务或操作。");
            if (data.TryGetProperty("data", out var taskData)) _ = SunoAudioJson.ResultData(taskData, query.TaskId);
        }
        else _ = SunoAudioJson.ResultData(data, query.TaskId);
        cancellationToken.ThrowIfCancellationRequested(); return root;
    }
    /// <summary>每次显式调用只进行一次 HTTP 交换，完整读取及资源释放成功后才解释状态。</summary>
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        System.Net.HttpStatusCode status; byte[] bytes; string? contentType; cancellationToken.ThrowIfCancellationRequested();
        using (var message = new HttpRequestMessage(method, new Uri(_root, path)))
        {
            if (_authentication == SunoAudioAuthentication.Bearer) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            message.Headers.Accept.ParseAdd("application/json");
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(_client); cancellationToken.ThrowIfCancellationRequested();
            if (_authentication == SunoAudioAuthentication.Anonymous) client.DefaultRequestHeaders.Authorization = null;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode; contentType = response.Content.Headers.ContentType?.ToString();
            bytes = await HttpBodyReader.ReadAsync(response.Content, _responseLimit, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)status is < 200 or >= 300) throw new SunoAudioException(status, null, bytes, contentType);
        var root = SunoAudioJson.Parse(bytes, contentType); var code = SunoAudioJson.Required(root, "code");
        if (code != "success") throw new SunoAudioException(status, code, bytes, contentType, root);
        return root;
    }
    /// <summary>查询字段保持原文，禁止任意操作及路径控制。</summary>
    private static void Validate(AudioTaskQuery query)
    { ArgumentNullException.ThrowIfNull(query); SunoAudioJson.TaskId(query.TaskId); SunoAudioJson.Operation(query.Operation); }
    /// <summary>本地限制原生 UTF-8 请求，不做上游生成参数默认化。</summary>
    private byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > _requestLimit) throw new ArgumentException("Suno-API 原生请求超过字节上限。");
        return bytes;
    }
}
