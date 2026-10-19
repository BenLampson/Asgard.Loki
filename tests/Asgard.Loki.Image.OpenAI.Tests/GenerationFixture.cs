using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>生成协议独立完整样本，不从生产编码器或编辑样本生成期望。</summary>
internal static class GenerationFixture
{
    internal static OpenAiImageOptions Options => new()
    {
        Id = "generation-target", Model = "native-generation-model", ApiKey = "fixture-generation-secret",
        BaseAddress = new("https://images.invalid/tenant/v1/"), Organization = "org-generation", Project = "project-generation", HttpClientName = "fixture-generation"
    };
    internal static ImageGenerationRequest Request(bool streaming = false)
    {
        var request = JsonSerializer.Deserialize<ImageGenerationRequest>(EditFixture.Text("generation-input"))!;
        if (streaming) { request.Parameters["n"] = JsonSerializer.SerializeToElement(1); request.Parameters["partial_images"] = JsonSerializer.SerializeToElement(2); }
        return request;
    }
    internal static ILokiClient Build(params IImageGenerationAdapter[] targets) => new LokiClientBuilder().AddImage(new()
    { Routes = [new() { Model = "pictures", Targets = targets }] }).Build();
    internal static string StreamBody
    {
        get
        {
            var assembly = typeof(GenerationFixture).Assembly;
            using var resource = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".generation-stream.sse", StringComparison.Ordinal)))!;
            using var reader = new StreamReader(resource); return reader.ReadToEnd();
        }
    }
    internal static string Partial => "data: {\"type\":\"image_generation.partial_image\",\"partial_image_index\":0,\"b64_json\":\"AQID\"}\n\n";
    internal static string Completed => "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"AwQFBg==\"}\n\n";
    internal static HttpResponseMessage Response(bool streaming, string? text = null)
        => EditFixture.Response(new EditReadStream(Encoding.UTF8.GetBytes(text ?? (streaming ? StreamBody : EditFixture.Text("generation-response")))),
            streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8");
    internal static async Task<List<ImageGenerationEvent>> Collect(IAsyncEnumerable<ImageGenerationEvent> source)
    { List<ImageGenerationEvent> seen = []; await foreach (var item in source) seen.Add(item); return seen; }
}
