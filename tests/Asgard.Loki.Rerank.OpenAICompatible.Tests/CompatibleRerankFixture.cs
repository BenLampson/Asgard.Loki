using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Rerank.OpenAICompatible.Tests;

/// <summary>独立原生协议样本，不通过生产适配器生成预期请求或响应。</summary>
internal static class CompatibleRerankFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(CompatibleRerankFixture).Assembly.GetManifestResourceStream(typeof(CompatibleRerankFixture).Namespace + ".Fixtures.native.json")!;
        using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    internal static RerankRequest Request(int index = 0) => new()
    { Model = "public-ranker", Parameters = Part("requests")[index].EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) };
    internal static OpenAiCompatibleRerankOptions Options(OpenAiCompatibleRerankAuthentication authentication = OpenAiCompatibleRerankAuthentication.BearerToken,
        string? key = null, string root = "https://rerank.example.invalid/proxy/v1/", int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024) => new()
    {
        Id = "compatible-one", Model = "caller-rerank-model", BaseAddress = new(root, UriKind.RelativeOrAbsolute), Authentication = authentication,
        ApiKey = key ?? (authentication == OpenAiCompatibleRerankAuthentication.Anonymous ? "" : "fixture-compatible-token"), HttpClientName = "compatible-rerank-tests",
        MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit
    };
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json") };
    internal static CompatibleRerankHttpFactory Factory(int index = 0) => new()
    { OnSend = (_, _) => Task.FromResult(Json(Part("responses")[index])) };
}
