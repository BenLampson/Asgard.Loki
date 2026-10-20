using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>独立原生 JSON/SSE 样本，不由供应商实现生产期望值。</summary>
internal static class ArkFixture
{
    internal static byte[] Bytes(string name)
    {
        using var source = typeof(ArkFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Volcengine.Tests.Fixtures." + name)!;
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }
    internal static JsonElement Data() { using var document = JsonDocument.Parse(Bytes("ark-images.json")); return document.RootElement.Clone(); }
    internal static JsonElement Part(string name) => Data().GetProperty(name);
    internal static VolcengineNativeImageRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Part("requests")[index].GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { PublicModel = "pictures", Parameters = parameters };
    }
    internal static VolcengineImageOptions Options(int maximum = 64 * 1024 * 1024, int requestLimit = 32 * 1024 * 1024, string key = "fixture-ark-image-secret")
        => new() { Id = "ark-test", Model = "fixture-endpoint-model", ApiKey = key, BaseAddress = new("https://ark-images.example.invalid/prefix/api/v3/"),
            HttpClientName = "ark-image-test", MaxResponseBytes = maximum, MaxRequestBytes = requestLimit };
    internal static ILokiClient Build(IHttpClientFactory factory) => new LokiClientBuilder().AddImage(new()
    { Routes = [new() { Model = "pictures", Targets = [new VolcengineImageAdapter(factory, Options())] }] }).Build();
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Sse(Stream source) => new(HttpStatusCode.OK)
    { Content = new StreamContent(source) { Headers = { ContentType = new("text/event-stream") { CharSet = "utf-8" } } } };
}
