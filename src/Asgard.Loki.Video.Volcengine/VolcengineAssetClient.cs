using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Video.Volcengine.Internal;

namespace Asgard.Loki.Video.Volcengine;

/// <summary>方舟私域媒体素材及素材组的十个原生操作，不启动预处理轮询、分页遍历或媒体下载。</summary>
public sealed class VolcengineAssetClient
{
    private const string Version = "2024-01-01";
    private readonly IHttpClientFactory _factory;
    private readonly TimeProvider _clock;
    private readonly Uri _root;
    private readonly string _accessKey;
    private readonly string _secret;
    private readonly string? _token;
    private readonly string _region;
    private readonly string _client;
    private readonly int _requestLimit;
    private readonly int _responseLimit;
    /// <summary>捕获完整独立认证配置，借用工厂与时钟，不发现、刷新或借用其他能力的凭据。</summary>
    public VolcengineAssetClient(IHttpClientFactory factory, VolcengineAssetOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.AccessKeyId, options.SecretAccessKey, options.Region, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); VolcengineVideoHttp.Text(text); }
        if (!options.AccessKeyId.All(IsIdentityCharacter) || !options.Region.All(IsIdentityCharacter)
            || options.SecretAccessKey.Any(char.IsControl) || options.SecurityToken is { } token && (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)))
            throw new ArgumentException("素材签名凭据或区域格式无效。", nameof(options));
        if (options.SecurityToken is { } securityToken) VolcengineVideoHttp.Text(securityToken);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.AbsolutePath != "/"
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("素材服务地址必须是无凭据、查询和片段的 HTTP(S) 根地址。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _factory = factory; _clock = timeProvider ?? TimeProvider.System; _root = options.BaseAddress;
        _accessKey = options.AccessKeyId; _secret = options.SecretAccessKey; _token = options.SecurityToken;
        _region = options.Region; _client = options.HttpClientName; _requestLimit = options.MaximumRequestBytes; _responseLimit = options.MaximumResponseBytes;
    }
    /// <summary>创建原生素材组；账户权限及授权函前置要求由上游判断。</summary>
    public Task<VolcengineAssetResult> CreateAssetGroupAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("CreateAssetGroup", parameters, cancellationToken);
    /// <summary>从调用者提供的 URL 创建素材；返回 Id 不代表异步预处理已完成。</summary>
    public Task<VolcengineAssetResult> CreateAssetAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("CreateAsset", parameters, cancellationToken);
    /// <summary>读取一页原生素材组，调用者保存筛选条件并显式传递分页参数。</summary>
    public Task<VolcengineAssetResult> ListAssetGroupsAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("ListAssetGroups", parameters, cancellationToken);
    /// <summary>读取一页原生素材，保留 Items、NextToken 或页码元数据。</summary>
    public Task<VolcengineAssetResult> ListAssetsAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("ListAssets", parameters, cancellationToken);
    /// <summary>查询一次素材原生状态；只有 Active 表示已完成预处理，Failed 原样保留错误。</summary>
    public Task<VolcengineAssetResult> GetAssetAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("GetAsset", parameters, cancellationToken);
    /// <summary>查询单个原生素材组信息。</summary>
    public Task<VolcengineAssetResult> GetAssetGroupAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("GetAssetGroup", parameters, cancellationToken);
    /// <summary>显式更新素材组名称或描述，不自动同步本地业务数据。</summary>
    public Task<VolcengineAssetResult> UpdateAssetGroupAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("UpdateAssetGroup", parameters, cancellationToken);
    /// <summary>显式更新素材名称，保留原生未知参数及响应。</summary>
    public Task<VolcengineAssetResult> UpdateAssetAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("UpdateAsset", parameters, cancellationToken);
    /// <summary>明确删除一个素材，不执行清理策略、自动重试或关联任务删除。</summary>
    public Task<VolcengineAssetResult> DeleteAssetAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("DeleteAsset", parameters, cancellationToken);
    /// <summary>明确删除素材组及上游定义的组内素材，调用者负责确认删除范围。</summary>
    public Task<VolcengineAssetResult> DeleteAssetGroupAsync(JsonObject parameters, CancellationToken cancellationToken = default)
        => Invoke("DeleteAssetGroup", parameters, cancellationToken);
    /// <summary>复制原生正文后签名实际传输的字节；Action 和 Version 固定于显式方法。</summary>
    private Task<VolcengineAssetResult> Invoke(string action, JsonObject parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var snapshot = ArkAssetJson.Snapshot(parameters, action);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > _requestLimit) throw new ArgumentException("素材请求超过显式字节上限。", nameof(parameters));
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_root, "?Action=" + action + "&Version=" + Version));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json");
            VolcengineRequestSigner.Sign(message, bytes, _accessKey, _secret, _token, _region, "ark", _clock.GetUtcNow());
        }
        catch { message.Dispose(); throw; }
        var expectedId = action.StartsWith("Get", StringComparison.Ordinal) || action.StartsWith("Update", StringComparison.Ordinal) ? snapshot["Id"]!.GetValue<string>() : null;
        return SendAsync(message, action, expectedId, cancellationToken);
    }
    /// <summary>区分原生 API 错误与素材自身处理失败，最终释放和取消检查完成后才交付结果。</summary>
    private async Task<VolcengineAssetResult> SendAsync(HttpRequestMessage request, string action, string? expectedId, CancellationToken cancellationToken)
    {
        var reply = await VolcengineVideoHttp.SendAsync(_factory, _client, request, _responseLimit, cancellationToken).ConfigureAwait(false);
        if ((int)reply.Status is < 200 or >= 300) throw new VideoUpstreamException("方舟素材 HTTP 调用失败。", reply.Status, reply.Body, reply.ContentType);
        if (!VolcengineVideoHttp.IsJson(reply.ContentType)) throw new InvalidDataException("素材服务未返回原生 JSON。");
        var data = VolcengineVideoHttp.Parse(reply.Body);
        if (!data.TryGetProperty("ResponseMetadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) throw new InvalidDataException("素材响应缺少原生元数据。");
        if (metadata.TryGetProperty("Error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new VideoUpstreamException("方舟素材返回原生 API 错误。", reply.Status, reply.Body, reply.ContentType);
        if (VolcengineVideoHttp.RequiredText(metadata, "Action") != action || VolcengineVideoHttp.RequiredText(metadata, "Version") != Version
            || VolcengineVideoHttp.RequiredText(metadata, "Service") != "ark" || VolcengineVideoHttp.RequiredText(metadata, "Region") != _region)
            throw new InvalidDataException("素材响应不属于本次原生操作或区域。");
        if (!data.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("素材响应缺少原生 Result 对象。");
        if (action.StartsWith("List", StringComparison.Ordinal))
        {
            if (!result.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("素材列表响应缺少原生 Items 数组。");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("素材列表成员必须是原生对象。");
                _ = VolcengineVideoHttp.RequiredText(item, "Id");
            }
        }
        else if (!action.StartsWith("Delete", StringComparison.Ordinal))
        {
            var id = VolcengineVideoHttp.RequiredText(result, "Id");
            if (expectedId is not null && id != expectedId) throw new InvalidDataException("素材响应包含其他资源的 Id。");
            if (action == "GetAsset") _ = VolcengineVideoHttp.RequiredText(result, "Status");
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(action, data);
    }
    /// <summary>凭据标识和区域只接受不会改变认证语法的 ASCII 标识字符。</summary>
    private static bool IsIdentityCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
}
