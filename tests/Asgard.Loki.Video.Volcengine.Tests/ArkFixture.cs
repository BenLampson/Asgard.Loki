using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>使用公共 SDK 和替代 HTTP 验证完整独立原生视频样本。</summary>
internal static class ArkFixture
{
    /// <summary>只从测试资源读取预先独立固定的原生报文，不由适配器生成预期值。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(ArkFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Volcengine.Tests.Fixtures.ark.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>固定一个完整上游，所有地址和凭据都是离线占位。</summary>
    internal static VolcengineVideoOptions Options(string id = "target-a", string model = "configured-seedance-model", string key = "fixture-key-only",
        string root = "https://ark.invalid/api/v3/", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = id, Model = model, ApiKey = key, BaseAddress = new(root), MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>原生参数去掉只由路由配置的 model，其他字段全部保留。</summary>
    internal static VideoTaskRequest Request()
    {
        var parameters = JsonNode.Parse(Data().GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>从真实模块入口构造，不使用另一个能力的状态或缓存。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, VolcengineVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new VolcengineVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>模拟带分片网络读取的原生 JSON，不触发实际媒体下载。</summary>
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK)
        => Json(data.GetRawText(), status);
    /// <summary>允许构造无效原生窗口。</summary>
    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    /// <summary>原生查询结果独立覆盖所有已知失败和未知状态。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
