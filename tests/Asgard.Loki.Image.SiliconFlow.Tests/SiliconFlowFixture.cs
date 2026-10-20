using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.SiliconFlow.Tests;

/// <summary>独立原生 JSON 样本和虚构 HTTP 目标。</summary>
internal static class SiliconFlowFixture
{
    internal static byte[] Bytes()
    {
        using var source = typeof(SiliconFlowFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.SiliconFlow.Tests.Fixtures.siliconflow-images.json")!;
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }
    internal static JsonElement Part(string name) { using var document = JsonDocument.Parse(Bytes()); return document.RootElement.GetProperty(name).Clone(); }
    internal static ImageGenerationRequest Request(int index = 0)
    {
        var root = Part("requests")[index]; return new() { Model = "pictures", Prompt = root.GetProperty("prompt").GetString()!,
            Parameters = root.EnumerateObject().Where(item => item.Name is not ("model" or "prompt")).ToDictionary(item => item.Name, item => item.Value.Clone()) };
    }
    internal static SiliconFlowImageOptions Options(int responseLimit = 65536, int requestLimit = 32768) => new()
    { Id = "siliconflow-test", Model = "vendor/image-fixture", Endpoint = new("https://siliconflow-images.example.invalid/prefix/v1/images/generations"), ApiKey = "fixture-siliconflow-key", MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    internal static HttpResponseMessage Json(JsonElement root, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(root.GetRawText(), Encoding.UTF8, "application/json") };
    internal static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
