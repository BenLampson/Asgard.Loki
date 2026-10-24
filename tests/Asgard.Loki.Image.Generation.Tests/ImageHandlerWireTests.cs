using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Image.Ali;
using Asgard.Loki.Image.Azure;
using Asgard.Loki.Image.Google;
using Asgard.Loki.Image.GoogleVertex;
using Asgard.Loki.Image.MiniMax;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Image.SiliconFlow;
using Asgard.Loki.Image.Volcengine;
using Asgard.Loki.Image.Zhipu;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>独立检查各协议的完整线上字节形状及原始结果保真，不调用真实上游。</summary>
public sealed class ImageHandlerWireTests
{
    /// <summary>供应商 Handler 只做已声明的可靠映射，并保留未知字段、零、false 和大整数。</summary>
    [Theory]
    [InlineData("openai")] [InlineData("volcengine")] [InlineData("siliconflow")] [InlineData("minimax")]
    [InlineData("zhipu")] [InlineData("google")] [InlineData("vertex")] [InlineData("azure")] [InlineData("ali")]
    public async Task UnifiedRequest_UsesCorrectNativeContract(string protocol)
    {
        var (handler, request, expected, reply) = Case(protocol);
        using var http = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), body.RootElement), body.RootElement.GetRawText());
            if (protocol is "google" or "vertex")
            { Assert.Equal("fixture-key-one", message.Headers.GetValues("x-goog-api-key").Single()); Assert.Null(message.Headers.Authorization); }
            else if (protocol == "azure")
                Assert.Equal("fixture-key-one", message.Headers.GetValues("api-key").Single());
            else Assert.Equal("fixture-key-one", message.Headers.Authorization?.Parameter);
            return GenerationFixture.Response(reply);
        });
        var target = GenerationFixture.Target(handler);
        if (protocol == "vertex") target = target with { UpstreamModel = "publishers/google/models/native-one" };
        if (protocol == "azure") target = target with { Settings = new() { ["api_version"] = "2025-04-01-preview" } };
        var result = await GenerationFixture.Client(new GenerationHttpFactory(http), target).GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.Succeeded, result.State); Assert.Single(result.Images);
        Assert.Equal(Encoding.UTF8.GetBytes(reply), result.RawResponse!.Body.ToArray());
        Assert.Equal(200, result.RawResponse.StatusCode);
        Assert.Equal("native-one", result.Data.TryGetProperty("model", out var model) ? model.GetString() : "native-one");
        Assert.Equal(1, http.Calls);
    }

    private static (ILokiImageGenerationHandler Handler, LokiImageGenerationRequest Request, JsonObject Expected, string Reply) Case(string protocol)
    {
        var request = new LokiImageGenerationRequest { Model = "pictures", Prompt = "cat", NativeParameters = new() { ["future"] = null } };
        var root = new JsonObject { ["model"] = "native-one", ["prompt"] = "cat", ["future"] = null };
        var reply = GenerationFixture.Reply;
        ILokiImageGenerationHandler handler;
        switch (protocol)
        {
            case "openai":
                handler = new OpenAiImageGenerationHandler();
                request = request with { Count = 1, Size = new(1024, 1024), Quality = "high", OutputFormat = "png", Background = "transparent" };
                root["n"] = 1; root["size"] = "1024x1024"; root["quality"] = "high"; root["output_format"] = "png"; root["background"] = "transparent"; break;
            case "volcengine":
                handler = new VolcengineImageGenerationHandler();
                request = request with { Count = 1, Size = new(1024, 1024), Seed = 9007199254740993L, Watermark = false };
                root["sequential_image_generation"] = "disabled"; root["size"] = "1024x1024"; root["seed"] = 9007199254740993L; root["watermark"] = false; break;
            case "siliconflow":
                handler = new SiliconFlowImageGenerationHandler();
                request = request with { Count = 1, Size = new(1024, 1024), Seed = 9007199254740993L, Steps = 20, GuidanceScale = 0, NegativePrompt = "blur" };
                root["batch_size"] = 1; root["image_size"] = "1024x1024"; root["seed"] = 9007199254740993L;
                root["num_inference_steps"] = 20; root["guidance_scale"] = 0; root["negative_prompt"] = "blur";
                reply = """{ "images":[{"url":"https://cdn.example.invalid/a.png"}], "timings":{}, "seed":0 }"""; break;
            case "minimax":
                handler = new MiniMaxImageGenerationHandler();
                request = request with { Count = 1, AspectRatio = "16:9", Watermark = false };
                root["n"] = 1; root["aspect_ratio"] = "16:9"; root["aigc_watermark"] = false;
                reply = """{ "base_resp":{"status_code":0,"status_msg":"ok"}, "data":{"image_urls":["https://cdn.example.invalid/a.png"]}, "metadata":{"success_count":1} }"""; break;
            case "zhipu":
                handler = new ZhipuImageGenerationHandler();
                request = request with { Size = new(1024, 1024), Quality = "high", Watermark = false };
                root["size"] = "1024x1024"; root["quality"] = "high"; root["watermark_enabled"] = false; break;
            case "google":
            case "vertex":
                handler = protocol == "google" ? new GoogleImageGenerationHandler() : new GoogleVertexImageGenerationHandler();
                request = request with { AspectRatio = "16:9" };
                root = JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"cat"}]}],"generationConfig":{"imageConfig":{"aspectRatio":"16:9"}},"future":null}""")!.AsObject();
                reply = """{ "candidates":[{"content":{"role":"model","parts":[{"inlineData":{"mimeType":"image/png","data":"iVBORw0KGgo="}}]},"finishReason":"STOP"}], "usageMetadata":{"totalTokenCount":0} }"""; break;
            case "azure":
                handler = new AzureImageGenerationHandler(); request = request with { Count = 1, Size = new(1024, 1024) };
                root.Remove("model"); root["n"] = 1; root["size"] = "1024x1024"; break;
            case "ali":
                handler = new AliImageGenerationHandler(); request = request with { Count = 1, Seed = 9007199254740993L, Watermark = false };
                root = JsonNode.Parse("""{"model":"native-one","input":{"messages":[{"role":"user","content":[{"text":"cat"}]}]},"parameters":{"n":1,"seed":9007199254740993,"watermark":false},"future":null}""")!.AsObject();
                reply = """{ "output":{"choices":[{"finish_reason":"stop","message":{"content":[{"image":"https://cdn.example.invalid/a.png"}]}}]},"usage":{} }"""; break;
            default: throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        return (handler, request, root, reply);
    }

    /// <summary>完整原生调用固定指定目标，绕过统一能力表，且不会推进公共模型的选择器。</summary>
    [Fact]
    public async Task NativeCall_PinsTargetAndPreservesUnknownParameters()
    {
        using var http = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            if (message.RequestUri!.Host.StartsWith("two", StringComparison.Ordinal))
            {
                Assert.Equal(7, body.RootElement.GetProperty("n").GetInt32());
                Assert.Equal(9007199254740993L, body.RootElement.GetProperty("vendor_only").GetInt64());
            }
            return GenerationFixture.Response();
        });
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target(), GenerationFixture.Target(id: "two") with { Parameters = new Dictionary<string, LokiImageParameterRule>() });
        var native = await client.GenerateNativeAsync(new()
        {
            Model = "pictures", ProviderId = "two",
            Parameters = new() { ["prompt"] = "cat", ["n"] = 7, ["vendor_only"] = 9007199254740993L }
        }, TestContext.Current.CancellationToken);
        Assert.Equal("two", native.ProviderId);
        Assert.Equal("one", (await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken)).ProviderId);
    }

    /// <summary>火山原生无 prompt 操作不被统一类的必填 prompt 阻挡。</summary>
    [Fact]
    public async Task NativeArkLayerOperation_DoesNotInjectPrompt()
    {
        using var http = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.False(body.RootElement.TryGetProperty("prompt", out _));
            Assert.True(body.RootElement.GetProperty("layer_decomposition").GetBoolean());
            return GenerationFixture.Response();
        });
        var client = GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new VolcengineImageGenerationHandler()));
        await client.GenerateNativeAsync(new()
        { Model = "pictures", ProviderId = "one", Parameters = new() { ["image"] = "https://input.example.invalid/a.png", ["layer_decomposition"] = true } },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Calls);
    }
}
