using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Ali.Tests;

/// <summary>固定独立原生样本，通过公开 SDK 入口验证万相协议，不连接上游。</summary>
internal static class WanFixture
{
    /// <summary>读取实现前独立提交的完整原生 JSON 样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(WanFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Ali.Tests.Fixtures.wan.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>从固定原生样本去掉配置模型，保留所有其他原生字段。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>虚构的独立上游配置，可修改单一绑定条件。</summary>
    internal static AliVideoOptions Options(string model = "configured-wan-1", AliVideoOperation operation = AliVideoOperation.VideoGeneration,
        string root = "https://wan.invalid/api/v1/", string key = "fixture-wan-key", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "wan-target", Model = model, Operation = operation, ApiKey = key, BaseAddress = new(root), MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>独立能力注册，不借用 LLM 状态或缓存。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, AliVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new AliVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每个调用返回独立内容对象，避免测试共享已释放响应。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>构造可独立修改或损坏的原生响应。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    /// <summary>原生确认包含原始 PENDING 状态，不合成完成。</summary>
    internal static JsonElement Ack() => Data().GetProperty("submissions")[0].GetProperty("response");
    /// <summary>按固定顺序选择完整原生状态结果。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
