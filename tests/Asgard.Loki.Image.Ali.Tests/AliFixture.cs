using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Ali.Tests;

/// <summary>读取独立供应商样本；所有地址和凭据均为虚构离线值。</summary>
internal static class AliFixture
{
    internal static byte[] Bytes(string name)
    {
        using var stream = typeof(AliFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Ali.Tests.Fixtures." + name)!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(Bytes("ali-images.json")); return document.RootElement.GetProperty(name).Clone(); }
    internal static AliNativeImageRequest Request(int index = 0, bool streaming = false)
    {
        var body = JsonNode.Parse(Part("requests")[index].GetRawText())!.AsObject(); body.Remove("model");
        if (streaming) body["parameters"]!["enable_interleave"] = true;
        return new() { PublicModel = "pictures", Parameters = body };
    }
    internal static AliImageOptions Options(string key = "fixture-ali-secret", string model = "native-fixture-model", string root = "https://ali-image.example.invalid/workspace/api/v1/", int responseLimit = 65536, int requestLimit = 32768)
        => new() { Id = "ali-test", Model = model, ApiKey = key, BaseAddress = new(root), HttpClientName = "ali-image-tests", MaxResponseBytes = responseLimit, MaxRequestBytes = requestLimit };
    internal static HttpResponseMessage Json(JsonElement root, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(root.GetRawText(), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Sse(byte[] bytes) => new(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(bytes)) { Headers = { ContentType = new("text/event-stream") { CharSet = "utf-8" } } } };
    internal static async Task<List<ImageGenerationEvent>> Collect(IAsyncEnumerable<ImageGenerationEvent> stream)
    { var result = new List<ImageGenerationEvent>(); await foreach (var item in stream) result.Add(item); return result; }
    internal static JsonElement Element(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
