using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.XAI.Tests;

/// <summary>独立 xAI 原生编辑协议向量，使用明确虚构目标和凭据。</summary>
internal static class XaiFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(XaiFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.XAI.Tests.Fixtures.xai-edits.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.GetProperty(name).Clone();
    }
    internal static ImageEditRequest Request(int index = 0)
    {
        var data = Part("requests")[index]; return new() { Model = "pictures", Prompt = data.GetProperty("prompt").GetString()!,
            Parameters = data.EnumerateObject().Where(field => field.Name is not ("model" or "prompt")).ToDictionary(field => field.Name, field => field.Value.Clone()) };
    }
    internal static XaiImageEditOptions Options(int responseLimit = 65536, int requestLimit = 32768) => new()
    { Id = "xai-edits", Model = "native-xai-fixture", ApiKey = "fixture-xai-edit-key", Endpoint = new("https://xai-edits.example.invalid/prefix/v1/images/edits"), HttpClientName = "xai-edit-tests", MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
