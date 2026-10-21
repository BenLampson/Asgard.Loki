using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>独立素材原生样本和 Python 签名向量，仅使用虚构地址及凭据。</summary>
internal static class AssetFixture
{
    /// <summary>读取在客户端实现前固定的 JSON 资源。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(AssetFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Volcengine.Tests.Fixtures.ark-assets.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>按原生操作选择完整输入输出对。</summary>
    internal static JsonElement Case(string action) => Data().GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("action").GetString() == action);
    /// <summary>提供可独立修改的原生请求，保留整数精度和未知字段。</summary>
    internal static JsonObject Request(string action) => JsonNode.Parse(Case(action).GetProperty("request").GetRawText())!.AsObject();
    /// <summary>提供可独立修改的原生响应。</summary>
    internal static JsonObject Reply(string action) => JsonNode.Parse(Case(action).GetProperty("response").GetRawText())!.AsObject();
    /// <summary>固定独立的非真实 AK/SK 配置，不从环境读取秘密。</summary>
    internal static VolcengineAssetOptions Options(string host = "ark.invalid", string? token = null, int requestLimit = 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { AccessKeyId = "fixture-access", SecretAccessKey = "fixture-secret-only", SecurityToken = token, BaseAddress = new("https://" + host + "/"), MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>只调用公开的十个显式方法，不借用内部签名器或万能 Action 调用。</summary>
    internal static Task<VolcengineAssetResult> Call(VolcengineAssetClient client, string action, JsonObject request, CancellationToken cancellationToken = default) => action switch
    {
        "CreateAssetGroup" => client.CreateAssetGroupAsync(request, cancellationToken), "CreateAsset" => client.CreateAssetAsync(request, cancellationToken),
        "GetAssetGroup" => client.GetAssetGroupAsync(request, cancellationToken), "GetAsset" => client.GetAssetAsync(request, cancellationToken),
        "ListAssetGroups" => client.ListAssetGroupsAsync(request, cancellationToken), "ListAssets" => client.ListAssetsAsync(request, cancellationToken),
        "UpdateAssetGroup" => client.UpdateAssetGroupAsync(request, cancellationToken), "UpdateAsset" => client.UpdateAssetAsync(request, cancellationToken),
        "DeleteAssetGroup" => client.DeleteAssetGroupAsync(request, cancellationToken), "DeleteAsset" => client.DeleteAssetAsync(request, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
