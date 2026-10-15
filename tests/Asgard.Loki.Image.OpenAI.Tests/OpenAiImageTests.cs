using Asgard.Loki.Image;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>通过本地 HTTP 协议验证 OpenAI 图像生成。</summary>
public sealed class OpenAiImageTests
{
    /// <summary>公共模型、真实模型、提示词及透明背景等参数分别处理。</summary>
    [Fact]
    public async Task GenerateAsync_MapsModelAndPreservesImageData()
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://images.invalid/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("image-key", request.Headers.Authorization!.Parameter);
            Assert.Equal("project", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("actual-image-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("Draw a tree", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("transparent", body.RootElement.GetProperty("background").GetString());
            Assert.Equal("kept", body.RootElement.GetProperty("future").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\",\"revised_prompt\":\"original metadata\"},{\"url\":\"https://cdn.invalid/image.png\"}],\"usage\":{\"total_tokens\":7},\"future\":true}", Encoding.UTF8, "application/json")
            };
        });
        var adapter = Adapter(handler);
        await using var client = new LokiClientBuilder().AddImage(new ImageOptions
        { Routes = [new ImageRoute { Model = "pictures", Targets = [adapter] }] }).Build();
        var request = Request();
        var result = await client.Image.GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Images[0].Decode().ToArray());
        Assert.Equal("https://cdn.invalid/image.png", result.Images[1].Url);
        Assert.True(result.Data.GetProperty("future").GetBoolean());
        Assert.Equal(7, result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal("pictures", result.Metadata.PublicModel);
        Assert.Equal("actual-image-model", result.Metadata.UpstreamModel);
        Assert.Equal("pictures", request.Model);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>部分图像、未知事件和最终图像都保留，并支持网络任意分块。</summary>
    [Fact]
    public async Task StreamAsync_PreservesPartialFinalAndUnknownEvents()
    {
        var body = "event: image_generation.partial_image\nid: partial-1\ndata: {\"type\":\"image_generation.partial_image\",\"partial_image_index\":0,\"b64_json\":\"AQI=\"}\n\n"
            + "data: {\"type\":\"vendor.future\",\"value\":\"kept\"}\n\n"
            + "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"usage\":{\"total_tokens\":3}}\n\n";
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            return Sse(body);
        });
        var events = await CollectAsync(Adapter(handler).StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(3, events.Count);
        Assert.Equal("partial-1", events[0].EventId);
        Assert.Equal("AQI=", events[0].Data.GetProperty("b64_json").GetString());
        Assert.Equal("vendor.future", events[1].Type);
        Assert.Equal("AQID", events[2].Data.GetProperty("b64_json").GetString());
    }

    /// <summary>部分图像不能替代最终完成事件。</summary>
    [Fact]
    public async Task StreamAsync_RejectsMissingFinalImage()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Sse("data: {\"type\":\"image_generation.partial_image\",\"b64_json\":\"AQI=\"}\n\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(Adapter(handler).StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>错误保留状态和正文，不重试或下载任何 URL。</summary>
    [Fact]
    public async Task GenerateAsync_PropagatesErrorWithoutRetry()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":{\"code\":\"invalid_size\"}}") }));
        var failure = await Assert.ThrowsAsync<ImageUpstreamException>(() => Adapter(handler).GenerateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(400, failure.StatusCode);
        Assert.Contains("invalid_size", failure.ResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>扩展字段不能绕过公共模型和提示词。</summary>
    [Fact]
    public async Task GenerateAsync_RejectsReservedParameterOverride()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var request = Request() with { Parameters = new() { ["model"] = JsonSerializer.SerializeToElement("hidden") } };
        await Assert.ThrowsAsync<ArgumentException>(() => Adapter(handler).GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>构造独立图像供应商配置。</summary>
    private static OpenAiImageAdapter Adapter(StubHttpHandler handler) => new(new StubHttpClientFactory(handler), new OpenAiImageOptions
    { Model = "actual-image-model", ApiKey = "image-key", BaseAddress = new("https://images.invalid/v1/"), Project = "project" });
    /// <summary>创建带未知参数的图像请求。</summary>
    private static ImageGenerationRequest Request() => new()
    {
        Model = "pictures", Prompt = "Draw a tree", Parameters = new()
        {
            ["background"] = JsonSerializer.SerializeToElement("transparent"),
            ["future"] = JsonSerializer.SerializeToElement("kept")
        }
    };
    /// <summary>创建碎片化的图像 SSE 内容。</summary>
    private static HttpResponseMessage Sse(string body)
    {
        var content = new StreamContent(new FragmentedReadStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("Text/Event-Stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    /// <summary>枚举到终态，避免遗漏流结束时的校验。</summary>
    private static async Task<List<ImageGenerationEvent>> CollectAsync(IAsyncEnumerable<ImageGenerationEvent> source)
    {
        List<ImageGenerationEvent> items = [];
        await foreach (var item in source) items.Add(item);
        return items;
    }
}
