using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Midjourney.Tests;

/// <summary>在生产实现之前独立编写的图片代理完整原生协议样本。</summary>
internal static class MidjourneyFixture
{
    internal static JsonElement Part(string name)
    {
        using var source = typeof(MidjourneyFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Midjourney.Tests.Fixtures.midjourney-images.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    internal static JsonObject Body(int index = 0) => JsonNode.Parse(Part("submissions")[index].GetProperty("body").GetRawText())!.AsObject();
    internal static MidjourneyImageOptions Options(string? secret = "fixture-mj-secret", string root = "http://mj-images.example.invalid:8081/prefix/mj-fast/mj/",
        string id = "midjourney-test", string client = "midjourney-images-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = id, BaseAddress = new(root), ApiSecret = secret, HttpClientName = client, MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    internal static MidjourneyTestFactory Factory(string name = "submitted") => new() { OnSend = (_, _) => Task.FromResult(Json(Part(name))) };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
}
