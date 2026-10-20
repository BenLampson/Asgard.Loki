using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>对完整独立原生请求/响应验证，无真实供应商、图片下载或像素处理。</summary>
public sealed class ArkImageProtocolTests
{
    /// <summary>原生 image 形状、无提示词图层拆分、未知字段和固定 model 原样编码。</summary>
    [Theory] [InlineData(0, "success")] [InlineData(1, "mixed_result")] [InlineData(3, "layer_result")]
    public async Task NativeJson_MatchesFullWireAndOriginalResult(int index, string resultName)
    {
        var expected = ArkFixture.Part("requests")[index]; var response = ArkFixture.Part(resultName);
        using var factory = new ArkTestFactory { OnSend = async (message, token) =>
        {
            Target(message); var bytes = await message.Content!.ReadAsByteArrayAsync(token); using var document = JsonDocument.Parse(bytes);
            Assert.True(JsonElement.DeepEquals(expected, document.RootElement)); Assert.Contains("9007199254740993", Encoding.UTF8.GetString(bytes));
            Assert.Equal(index != 3, document.RootElement.TryGetProperty("prompt", out _)); return ArkFixture.Json(response);
        } };
        var result = await new VolcengineImageAdapter(factory, ArkFixture.Options()).GenerateNativeAsync(ArkFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(response.GetRawText(), result.Data.GetRawText()); Assert.Equal(new ImageResultMetadata("pictures", "fixture-endpoint-model", "ark-test"), result.Metadata);
        Assert.Equal("actual-provider-version", result.Data.GetProperty("model").GetString());
        if (resultName == "mixed_result") { Assert.Equal(3, result.Images.Count); Assert.True(result.Images[1].Data.TryGetProperty("error", out _)); }
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }

    /// <summary>统一便利入口也使用相同原生 JSON，不把参考图片转成 OpenAI 编辑/文件请求。</summary>
    [Fact]
    public async Task PublicImageRoute_PreservesNativeImageInputs()
    {
        using var factory = new ArkTestFactory(); await using var client = ArkFixture.Build(factory); var body = ArkFixture.Part("requests")[1];
        var request = new ImageGenerationRequest { Model = "pictures", Prompt = body.GetProperty("prompt").GetString()!,
            Parameters = body.EnumerateObject().Where(item => item.Name is not ("model" or "prompt")).ToDictionary(item => item.Name, item => item.Value.Clone()) };
        factory.OnSend = async (message, token) =>
        { using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(body, document.RootElement)); return ArkFixture.Json(ArkFixture.Part("success")); };
        await client.Image.GenerateAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }

    /// <summary>统一流入口的图像数组/组图字段也保持原生形状，未改造成其他供应商事件。</summary>
    [Fact]
    public async Task PublicStreamRoute_PreservesNativeGroupProtocol()
    {
        using var factory = new ArkTestFactory { OnSend = async (message, token) =>
        {
            using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(ArkFixture.Part("requests")[2], document.RootElement));
            return ArkFixture.Sse(new ArkReadStream(ArkFixture.Bytes("ark-group.sse")));
        } };
        await using var client = ArkFixture.Build(factory); var body = ArkFixture.Part("requests")[2];
        var request = new ImageGenerationRequest { Model = "pictures", Prompt = body.GetProperty("prompt").GetString()!,
            Parameters = body.EnumerateObject().Where(item => item.Name is not ("model" or "prompt")).ToDictionary(item => item.Name, item => item.Value.Clone()) };
        var seen = new List<ImageGenerationEvent>(); await foreach (var item in client.Image.StreamAsync(request, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(5, seen.Count); Assert.Contains(seen, item => item.Type == "image_generation.partial_failed"); Assert.Equal("image_generation.completed", seen[^1].Type); Assert.Equal(1, factory.Calls);
    }

    /// <summary>原生组图部分失败仍保留其他图片；单图无 summary 时不合成 completed 事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeSse_RetainsOriginalFramesAndTermination(bool grouped)
    {
        var frames = ArkFixture.Part(grouped ? "group_frames" : "single_frames"); using var source = new ArkReadStream(ArkFixture.Bytes(grouped ? "ark-group.sse" : "ark-single.sse"));
        using var factory = new ArkTestFactory { OnSend = async (message, token) =>
        {
            Target(message); Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            if (grouped) Assert.True(JsonElement.DeepEquals(ArkFixture.Part("requests")[2], body.RootElement)); return ArkFixture.Sse(source);
        } };
        var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options()); var seen = new List<ImageGenerationEvent>();
        await foreach (var item in adapter.StreamNativeAsync(ArkFixture.Request(grouped ? 2 : 0), TestContext.Current.CancellationToken))
        {
            var expected = frames[seen.Count]; Assert.Equal(expected.GetProperty("raw_data").GetString(), item.Data.GetRawText());
            Assert.Equal(expected.GetProperty("event").GetString(), item.EventName); Assert.Equal(expected.GetProperty("id").GetString(), item.EventId); Assert.Equal(0, item.RetryMilliseconds);
            if (!grouped || item.Type == "image_generation.completed") { Assert.True(source.Disposed); Assert.True(source.Eof); Assert.Equal(1, factory.Disposals); }
            else Assert.False(source.Eof);
            seen.Add(item);
        }
        Assert.Equal(frames.GetArrayLength(), seen.Count);
        if (grouped) { Assert.Contains(seen, item => item.Type == "image_generation.partial_failed"); Assert.False(seen[^1].Data.TryGetProperty("b64_json", out _)); }
        else Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed");
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>原生请求参数在调用时冻结，而不是等工厂或枚举时才读取。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeSnapshot_PrecedesFactoryAndEnumeration(bool streaming)
    {
        var request = ArkFixture.Request(); using var factory = new ArkTestFactory { OnSend = async (message, token) =>
        {
            using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal("9007199254740993", document.RootElement.GetProperty("Future").GetProperty("Big").GetRawText());
            return streaming ? ArkFixture.Sse(new ArkReadStream(ArkFixture.Bytes("ark-single.sse"))) : ArkFixture.Json(ArkFixture.Part("success"));
        } };
        factory.OnCreate = () => request.Parameters.Clear(); var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options());
        if (streaming) { var stream = adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken); request.Parameters.Clear(); await foreach (var _ in stream) { } }
        else await adapter.GenerateNativeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>只拒绝实际协议歧义，不按模型目录改写参数或将文件占位符转换为下载。</summary>
    [Theory] [InlineData("model")] [InlineData("image-object")] [InlineData("prompt")] [InlineData("layers-no-input")]
    [InlineData("false-stream")] [InlineData("limit")]
    public void InvalidNativeInput_FailsBeforeHttp(string kind)
    {
        using var factory = new ArkTestFactory(); var request = ArkFixture.Request();
        if (kind == "model") request.Parameters["Model"] = "override";
        if (kind == "image-object") request.Parameters["image"] = new JsonObject { ["__fileRef"] = "host-only" };
        if (kind == "prompt") request.Parameters.Remove("prompt");
        if (kind == "layers-no-input") request.Parameters["layer_decomposition"] = true;
        if (kind == "false-stream") request.Parameters["stream"] = false;
        var adapter = new VolcengineImageAdapter(factory, ArkFixture.Options(requestLimit: kind == "limit" ? 1 : 100000));
        Assert.ThrowsAny<ArgumentException>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }

    private static void Target(HttpRequestMessage message)
    {
        Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://ark-images.example.invalid/prefix/api/v3/images/generations", message.RequestUri!.AbsoluteUri);
        Assert.Equal(ArkFixture.Part("target").GetProperty("authorization").GetString(), message.Headers.Authorization!.ToString()); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
    }
}
