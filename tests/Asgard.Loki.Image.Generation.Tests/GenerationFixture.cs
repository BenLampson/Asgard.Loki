using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Image.OpenAI;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>独立构造请求与预期响应，避免用生产映射器生成期望数据。</summary>
internal static class GenerationFixture
{
    internal const string Reply = """{ "created":0, "data":[{"b64_json":"iVBORw0KGgo=","future":null}], "usage":{"exact":9007199254740993} }""";
    internal static HttpResponseMessage Response(string body = Reply) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    internal static LokiImageUpstreamProvider Target(ILokiImageGenerationHandler? handler = null, string id = "one") => new()
    {
        Id = id, UpstreamModel = "native-" + id, ApiKey = "fixture-key-" + id,
        BaseAddress = new Uri("https://" + id + ".example.invalid/v1/"),
        Handler = handler ?? new OpenAiImageGenerationHandler(),
        Parameters = Rules()
    };
    internal static Dictionary<string, LokiImageParameterRule> Rules() => new()
    {
        ["Count"] = new() { Minimum = 1, Maximum = 2 },
        ["Size"] = new() { AllowedValues = [JsonSerializer.SerializeToElement("1024x1024")] },
        ["Quality"] = new() { AllowedValues = [JsonSerializer.SerializeToElement("high")] },
        ["Seed"] = new(),
        ["Steps"] = new() { Minimum = 1, Maximum = 50 },
        ["GuidanceScale"] = new() { Minimum = 0, Maximum = 20 },
        ["Watermark"] = new(),
        ["NegativePrompt"] = new(),
        ["AspectRatio"] = new() { AllowedValues = [JsonSerializer.SerializeToElement("16:9")] },
        ["OutputFormat"] = new() { AllowedValues = [JsonSerializer.SerializeToElement("png")] },
        ["Background"] = new() { AllowedValues = [JsonSerializer.SerializeToElement("transparent")] }
    };
    internal static LokiImageProvider Registration(params LokiImageUpstreamProvider[] targets) => new()
    { Models = [new() { Name = "pictures", Providers = targets }] };
    internal static LokiImageClient Client(IHttpClientFactory factory, params LokiImageUpstreamProvider[] targets)
        => new LokiImageFactory(Registration(targets.Length == 0 ? [Target()] : targets), factory).CreateClient();
    internal static LokiImageGenerationRequest Request() => new()
    {
        Model = "pictures", Prompt = "原始提示 😀", Count = 1, Size = new(1024, 1024),
        NativeParameters = new() { ["future"] = new JsonObject { ["exact"] = 9007199254740993L, ["off"] = false, ["none"] = null } }
    };
}
