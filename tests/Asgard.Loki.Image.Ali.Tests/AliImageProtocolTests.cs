using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Ali.Tests;

/// <summary>原生完整请求、同步结果和图文流的独立离线契约。</summary>
public sealed class AliImageProtocolTests
{
    /// <summary>完整请求、原生结果和图像视图保持字段与数值精度。</summary>
    [Fact]
    public async Task NativeJsonPreservesWholeRequestAndNativeImageView()
    {
        using var factory = new AliTestFactory();
        factory.OnSend = async (message, _) =>
        {
            Assert.Equal("https://ali-image.example.invalid/workspace/api/v1/services/aigc/multimodal-generation/generation", message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-ali-secret", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("X-DashScope-Async"));
            Assert.False(message.Headers.Contains("X-DashScope-SSE"));
            Assert.True(JsonElement.DeepEquals(AliFixture.Part("requests")[0], AliFixture.Element(await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))));
            return AliFixture.Json(AliFixture.Part("success"));
        };
        var result = await new AliImageAdapter(factory, AliFixture.Options()).GenerateNativeAsync(AliFixture.Request(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(AliFixture.Part("success"), result.Data));
        var image = Assert.Single(result.Images); Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", image.Url);
        Assert.False(image.Data.TryGetProperty("url", out _)); Assert.False(image.Data.GetProperty("future").GetBoolean());
        Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("input_tokens").GetRawText());
        Assert.Equal(new ImageResultMetadata("pictures", "native-fixture-model", "ali-test"), result.Metadata); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>公共图像路由使用原生消息和固定目标模型。</summary>
    [Fact]
    public async Task PublicRouteUsesNativeMessagesAndBoundModel()
    {
        using var factory = new AliTestFactory();
        factory.OnSend = async (message, _) =>
        {
            var root = AliFixture.Element(await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("native-fixture-model", root.GetProperty("model").GetString());
            Assert.Equal("中文 prompt", root.GetProperty("input").GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.False(root.GetProperty("parameters").GetProperty("watermark").GetBoolean()); return AliFixture.Json(AliFixture.Part("success"));
        };
        await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new AliImageAdapter(factory, AliFixture.Options())] }] }).Build();
        var result = await client.Image.GenerateAsync(new() { Model = "pictures", Prompt = "中文 prompt", Parameters = new() { ["parameters"] = AliFixture.Element("""{"watermark":false}""") } }, TestContext.Current.CancellationToken);
        Assert.Single(result.Images);
    }
    /// <summary>逐帧对照独立样本，冻结请求并保留关联与累计用量。</summary>
    [Fact]
    public async Task StreamPreservesFramesChoiceIndicesAndCumulativeUsage()
    {
        using var factory = new AliTestFactory();
        factory.OnSend = async (message, _) =>
        {
            Assert.Equal("enable", Assert.Single(message.Headers.GetValues("X-DashScope-SSE"))); Assert.False(message.Headers.Contains("X-DashScope-Async"));
            var root = AliFixture.Element(await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)); Assert.True(root.GetProperty("parameters").GetProperty("stream").GetBoolean());
            Assert.True(JsonElement.DeepEquals(AliFixture.Part("requests")[0].GetProperty("input"), root.GetProperty("input")));
            return AliFixture.Sse(AliFixture.Bytes("ali-images.sse"));
        };
        var request = AliFixture.Request(streaming: true); var stream = new AliImageAdapter(factory, AliFixture.Options()).StreamNativeAsync(request, TestContext.Current.CancellationToken);
        request.Parameters["input"] = new JsonObject();
        var events = await AliFixture.Collect(stream); Assert.Equal(3, events.Count);
        var expected = Encoding.UTF8.GetString(AliFixture.Bytes("ali-images.sse")).Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => AliFixture.Element(line[6..].TrimEnd('\r'))).ToArray();
        for (var i = 0; i < events.Count; i++) Assert.True(JsonElement.DeepEquals(expected[i], events[i].Data));
        Assert.True(events[0].Data.GetProperty("output").GetProperty("finished").GetBoolean());
        Assert.Equal(7, events[1].Data.GetProperty("output").GetProperty("choices")[0].GetProperty("index").GetInt32());
        Assert.Equal(2, events[2].Data.GetProperty("output").GetProperty("choices")[0].GetProperty("index").GetInt32());
        Assert.Equal(9, events[2].Data.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal("result", events[2].EventName); Assert.Equal("event-2", events[2].EventId); Assert.Equal(500, events[2].RetryMilliseconds);
        Assert.False(events[2].Data.GetProperty("future").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AliFixture.Collect(stream)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>缺省、空字符串和 null 错误码保持成功语义。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task EmptyNativeCodeIsNotAnError(string code)
    {
        var data = JsonNode.Parse(AliFixture.Part("success").GetRawText())!.AsObject();
        if (code.Length > 0) data["code"] = JsonNode.Parse(code);
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Element(data.ToJsonString()))) };
        Assert.Single((await new AliImageAdapter(factory, AliFixture.Options()).GenerateNativeAsync(AliFixture.Request(), TestContext.Current.CancellationToken)).Images);
    }
    /// <summary>原生纯文本结果有效且不虚构图片。</summary>
    [Fact]
    public async Task TextOnlyResultRemainsValidAndUnmodified()
    {
        var data = AliFixture.Element("""{"output":{"finished":true,"choices":[{"finish_reason":"stop","message":{"content":[{"text":"text only"}]}}]}}""");
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(data)) };
        var result = await new AliImageAdapter(factory, AliFixture.Options()).GenerateNativeAsync(AliFixture.Request(), TestContext.Current.CancellationToken); Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(data, result.Data));
    }
    /// <summary>原生 choice 的可选索引可与缺省数组位置共存；不能因未知索引字段出现而丢掉合法帧。</summary>
    [Fact]
    public async Task OptionalChoiceIndicesMayAppearWithoutChangingAssociation()
    {
        var text = "data: {\"output\":{\"choices\":[{\"message\":{\"content\":[{\"text\":\"zero\"}]}},{\"index\":7,\"message\":{\"content\":[{\"text\":\"seven\"}]}}]}}\n\n"
            + "data: {\"output\":{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\"},{\"index\":7,\"finish_reason\":\"stop\"}]}}\n\n";
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Sse(Encoding.UTF8.GetBytes(text))) };
        var result = await AliFixture.Collect(new AliImageAdapter(factory, AliFixture.Options()).StreamNativeAsync(AliFixture.Request(streaming: true), TestContext.Current.CancellationToken));
        Assert.Equal(2, result.Count); Assert.False(result[0].Data.GetProperty("output").GetProperty("choices")[0].TryGetProperty("index", out _));
    }

}
