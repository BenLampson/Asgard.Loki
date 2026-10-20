using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Rerank.SGLang.Tests;

/// <summary>独立原生协议样本，不通过生产适配器生成预期请求或响应。</summary>
internal static class SgLangFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(SgLangFixture).Assembly.GetManifestResourceStream(typeof(SgLangFixture).Namespace + ".Fixtures.native.json")!;
        using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    internal static RerankRequest Request(int index = 0) => new()
    { Model = "public-ranker", Parameters = Part("requests")[index].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) };
    internal static SgLangRerankOptions Options(SgLangRerankAuthentication authentication = SgLangRerankAuthentication.BearerToken,
        string? key = null, string root = "https://rerank.example.invalid/proxy/v1/", int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024) => new()
    {
        Id = "sglang-one", Model = "caller-deployed-model", BaseAddress = new(root, UriKind.RelativeOrAbsolute), Authentication = authentication,
        ApiKey = key ?? (authentication == SgLangRerankAuthentication.Anonymous ? "" : "fixture-sglang-token"), HttpClientName = "sglang-rerank-tests",
        MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit
    };
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json") };
    internal static SgLangHttpFactory Factory(int index = 0) => new()
    { OnSend = (_, _) => Task.FromResult(Json(Part("responses")[index])) };
}
