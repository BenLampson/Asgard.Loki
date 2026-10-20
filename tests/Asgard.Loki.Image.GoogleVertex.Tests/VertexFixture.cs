using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>完整独立 Vertex 原生资源/认证/图片向量，不联系真实上游。</summary>
internal static class VertexFixture
{
    internal static byte[] Bytes(string name)
    {
        using var source = typeof(VertexFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.GoogleVertex.Tests.Fixtures." + name)!;
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(Bytes("vertex-images.json")); return document.RootElement.GetProperty(name).Clone(); }
    internal static GoogleVertexNativeImageRequest Request(int transport = 0) => new() { PublicModel = "pictures", Parameters = JsonNode.Parse(Part("transports")[transport].GetProperty("request").GetRawText())!.AsObject() };
    internal static GoogleVertexImageOptions Options(bool key = false, bool express = false, int responseLimit = 65536, int requestLimit = 32768) => new()
    {
        Id = "vertex-image", ModelResource = express ? "publishers/google/models/image-fixture" : "projects/project-fixture/locations/global/publishers/google/models/image-fixture",
        BaseAddress = new("https://vertex-images.example.invalid/prefix/v1/"), PrincipalIdentity = key ? null : "fixture-principal", ApiKey = key ? "fixture-image-key" : "",
        QuotaProjectId = key ? null : "quota-fixture", HttpClientName = "vertex-image-tests", MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit
    };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Sse(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new MemoryStream(bytes)) { Headers = { ContentType = new("text/event-stream") { CharSet = "utf-8" } } } };
    internal static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    internal static async Task<List<ImageGenerationEvent>> Collect(IAsyncEnumerable<ImageGenerationEvent> source)
    { var items = new List<ImageGenerationEvent>(); await foreach (var item in source) items.Add(item); return items; }
}
