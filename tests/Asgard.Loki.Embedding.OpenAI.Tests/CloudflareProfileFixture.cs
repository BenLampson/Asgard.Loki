using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>独立规范 Cloudflare 索引 JSON 目标和完整样本，不从生产编码器产生期望值。</summary>
internal static class CloudflareProfileFixture
{
    internal static JsonElement Part(string name)
    {
        using var source = typeof(CloudflareProfileFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Embedding.OpenAI.Tests.Fixtures.cloudflare-profile.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    internal static EmbeddingRequest Request(int index = 0) => new() { Model = "vectors", Parameters = Part("requests")[index].EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone()) };
    internal static OpenAiEmbeddingOptions Options(int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024)
    {
        var profile = Part("profile"); return new() { Id = profile.GetProperty("id").GetString()!, Model = profile.GetProperty("model").GetString()!, ApiKey = profile.GetProperty("api_key").GetString()!,
            BaseAddress = new(profile.GetProperty("base_address").GetString()!), HttpClientName = profile.GetProperty("http_client_name").GetString()!, MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    }
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static EmbeddingTransportFactory Factory(int index = 1) => new() { OnSend = (_, _) => Task.FromResult(Json(Part("responses")[index])) };
}
