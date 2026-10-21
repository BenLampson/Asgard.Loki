using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Replicate.Tests;

/// <summary>独立原生预测与模型输入上传样本。</summary>
internal static class ReplicateFixture
{
    internal static JsonElement Part(string name)
    {
        using var source = typeof(ReplicateFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Replicate.Tests.Fixtures.replicate-images.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    internal static ReplicateNativeImageRequest Native() => new() { PublicModel = "pictures", Parameters = JsonNode.Parse(Part("request").GetRawText())!.AsObject() };
    internal static ImageGenerationRequest Request()
    {
        var input = Part("request").GetProperty("input");
        return new() { Model = "pictures", Prompt = input.GetProperty("prompt").GetString()!, Parameters = input.EnumerateObject().Where(p => p.Name != "prompt").ToDictionary(p => p.Name, p => p.Value.Clone()) };
    }
    internal static ReplicateImageOptions Options(int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024, long uploadLimit = 32 * 1024 * 1024,
        string root = "https://replicate-images.example.invalid/prefix/v1/", string model = "fixture-owner/image-model", string key = "fixture-replicate-key")
        => new() { Id = "replicate-test", Model = model, ApiKey = key, BaseAddress = new(root), HttpClientName = "replicate-image-test",
            MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit, MaxUploadBytes = uploadLimit };
    internal static ReplicateTestFactory Factory(string result = "success") => new() { OnSend = (_, _) => Task.FromResult(Json(Part(result))) };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
}
