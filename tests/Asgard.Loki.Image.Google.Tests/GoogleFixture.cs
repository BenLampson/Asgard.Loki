using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Google.Tests;

/// <summary>独立 Gemini JSON/SSE 离线向量及明确的虚构完整目标。</summary>
internal static class GoogleFixture
{
    internal static byte[] Bytes(string name)
    {
        using var stream = typeof(GoogleFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Google.Tests.Fixtures." + name)!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(Bytes("gemini-images.json")); return document.RootElement.GetProperty(name).Clone(); }
    internal static GoogleNativeImageRequest Request() => new() { PublicModel = "pictures", Parameters = JsonNode.Parse(Part("request").GetRawText())!.AsObject() };
    internal static GoogleImageOptions Options(int responseLimit = 65536, int requestLimit = 32768, string model = "image-fixture-model") => new()
    { Id = "google-images", Model = model, ApiKey = "fixture-google-image-secret", BaseAddress = new("https://google-images.example.invalid/prefix/v1beta/"),
        HttpClientName = "google-image-test", MaxResponseBytes = responseLimit, MaxRequestBytes = requestLimit };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Sse(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new MemoryStream(bytes)) { Headers = { ContentType = new("text/event-stream") { CharSet = "utf-8" } } } };
    internal static JsonElement Element(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    internal static async Task<List<ImageGenerationEvent>> Collect(IAsyncEnumerable<ImageGenerationEvent> stream)
    { var result = new List<ImageGenerationEvent>(); await foreach (var item in stream) result.Add(item); return result; }
}
