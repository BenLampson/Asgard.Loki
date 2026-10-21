using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.XAI.Tests;

/// <summary>先于实现提交的独立原生样本及仅通过公开入口构造的虚构固定目标。</summary>
internal static class XaiFixture
{
    /// <summary>完整原始 JSON，不由被测序列化器生成期望数据。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(XaiFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.XAI.Tests.Fixtures.xai.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>原生 model 移交目标配置，其他参数保持原样。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Case(index).GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>七个独立原生请求与确认。</summary>
    internal static JsonElement Case(int index) => Data().GetProperty("submissions")[index];
    /// <summary>保留所有原生状态的完整窗口。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
    /// <summary>构造独立原生状态或空正文 HTTP 窗口。</summary>
    internal static HttpResponseMessage QueryResponse(int index)
    {
        var query = Query(index); var status = (HttpStatusCode)query.GetProperty("http_status").GetInt32();
        return query.TryGetProperty("body", out var body) ? Json(body, status) : new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(query.GetProperty("body_text").GetString()!)) };
    }
    /// <summary>可单独更改目标某一部分的配置。</summary>
    internal static XaiVideoOptions Options(XaiVideoOperation operation = XaiVideoOperation.Generate, string model = "grok-imagine-video", string root = "https://xai.invalid/",
        string key = "fixture-xai-key", string client = "xai-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "xai-target", Model = model, Operation = operation, ApiKey = key, BaseAddress = new(root), HttpClientName = client,
            MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>注册独立 Video 能力和完整供应商目标。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, XaiVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new XaiVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每次原生响应单独分配资源。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>构造完整或损坏的原生响应。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
