using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Vidu.Tests;

/// <summary>先于实现提交的独立原生样本及仅通过公开入口构造的虚构固定目标。</summary>
internal static class ViduFixture
{
    /// <summary>完整原始 JSON，不由被测序列化器生成期望数据。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(ViduFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Vidu.Tests.Fixtures.vidu.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>原生 model 移交目标配置，其他参数保持原样。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Case(index).GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>四种独立原生请求与确认。</summary>
    internal static JsonElement Case(int index) => Data().GetProperty("submissions")[index];
    /// <summary>保留所有原生状态的完整窗口。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
    /// <summary>可单独更改目标某一部分的配置。</summary>
    internal static ViduVideoOptions Options(ViduVideoOperation operation = ViduVideoOperation.TextToVideo, string model = "viduq1", string root = "https://vidu.invalid/",
        string key = "fixture-vidu-key", string client = "vidu-test", int requestLimit = 20 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "vidu-target", Model = model, Operation = operation, ApiKey = key, BaseAddress = new(root), HttpClientName = client,
            MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>注册独立 Video 能力和完整供应商目标。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, ViduVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new ViduVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每次原生响应单独分配资源。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>构造完整或损坏的原生响应。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
