using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>独立原生样本和虚构完整目标，不连接真实供应商。</summary>
internal static class VertexVideoFixture
{
    internal const string Resource = "projects/fixture-project/locations/us-central1/publishers/google/models/veo-3.1-generate-001";
    internal const string Operation = Resource + "/operations/fixture-op-01";
    /// <summary>读取完整原生 JSON 样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(VertexVideoFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.GoogleVertex.Tests.Fixtures.veo-vertex.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>保持原生请求结构，不从模型名称推断输入形状。</summary>
    internal static VideoTaskRequest Request(int index = 0) => new() { Model = "video", Parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetRawText())!.AsObject() };
    /// <summary>构造可单独修改一项绑定条件的独立目标。</summary>
    internal static GoogleVertexVideoOptions Options(string resource = Resource, string principal = "fixture-principal", string root = "https://vertex.invalid/v1/",
        string? quota = null, string client = "vertex-video-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 128 * 1024 * 1024)
        => new() { Id = "vertex-video-target", ModelResource = resource, PrincipalIdentity = principal, BaseAddress = new(root), QuotaProjectId = quota,
            HttpClientName = client, MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>经由公开能力入口验证任务与完整目标引用。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, VertexVideoTokenSource source, VertexVideoClock clock, GoogleVertexVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new GoogleVertexVideoAdapter(factory, source, options ?? Options(), clock)] }] }).Build();
    /// <summary>构造由每个调用拥有的独立响应。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>支持构造损坏的原生正文。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    /// <summary>固定样本中的完整原生状态结果。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
