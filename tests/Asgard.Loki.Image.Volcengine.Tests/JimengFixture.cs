using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>独立 Visual 原生 JSON 和签名期望值，不调用生产签名器构造结果。</summary>
internal static class JimengFixture
{
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(ArkFixture.Bytes("jimeng-images.json")); return document.RootElement.GetProperty(name).Clone(); }
    internal static JsonElement Element(string json)
    { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    internal static JimengImageOptions Options(string? token = null, int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024,
        string root = "https://jimeng-images.example.invalid:9443/", string key = "fixture-ak", string secret = "fixture-secret-key")
        => new() { Id = "jimeng-test", RequestKey = "jimeng_high_aes_general_v21_L", AccessKeyId = key, SecretAccessKey = secret,
            SecurityToken = token, BaseAddress = new(root), HttpClientName = "jimeng-image-test", MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    internal static ImageGenerationRequest Request(bool minimal = false)
    {
        var source = Part("request");
        return new() { Model = "pictures", Prompt = source.GetProperty("prompt").GetString()!, Parameters = minimal ? []
            : source.EnumerateObject().Where(p => p.Name is not ("req_key" or "prompt")).ToDictionary(p => p.Name, p => p.Value.Clone()) };
    }
    internal static ArkTestFactory Factory(string result = "url_result")
        => new() { OnSend = (_, _) => Task.FromResult(Json(Part(result))) };
    internal static JimengImageAdapter Adapter(IHttpClientFactory factory, JimengImageOptions? options = null)
        => new(factory, options ?? Options(), new JimengTestClock());
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
}
