using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>六种原生传输及完整图片/签名/安全结果的独立协议契约。</summary>
public sealed class VertexImageProtocolTests
{
    /// <summary>完整资源/Bearer、项目 Key、Express Key 的 JSON/SSE 逐字段匹配独立向量。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task NativeTransportMatchesIndependentWire(int index)
    {
        var expected = VertexFixture.Part("transports")[index]; var stream = expected.GetProperty("stream").GetBoolean(); var key = index >= 2;
        using var source = new VertexTestTokenSource(); using var factory = new VertexTestFactory();
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(expected.GetProperty("url").GetString(), message.RequestUri!.AbsoluteUri);
            foreach (var header in expected.GetProperty("headers").EnumerateObject()) Assert.Equal(header.Value.GetString(), Assert.Single(message.Headers.GetValues(header.Name)));
            Assert.Equal(!key, message.Headers.Authorization is not null); Assert.Equal(key, message.Headers.Contains("x-goog-api-key")); Assert.False(message.RequestUri.Query.Contains("key=", StringComparison.Ordinal));
            Assert.Equal(!key, message.Headers.Contains("x-goog-user-project"));
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("request"), VertexFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            return stream ? VertexFixture.Sse(VertexFixture.Bytes("vertex-images.sse")) : VertexFixture.Json(VertexFixture.Part("response"));
        };
        var options = VertexFixture.Options(key, index >= 4); var adapter = key ? new GoogleVertexImageAdapter(factory, options) : new GoogleVertexImageAdapter(factory, source, options, new VertexTestTime());
        if (stream)
        {
            var result = await VertexFixture.Collect(adapter.StreamNativeAsync(VertexFixture.Request(index), TestContext.Current.CancellationToken)); Assert.Equal(4, result.Count);
            var frames = Encoding.UTF8.GetString(VertexFixture.Bytes("vertex-images.sse")).Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => VertexFixture.Element(line[6..].TrimEnd('\r'))).ToArray();
            for (var i = 0; i < frames.Length; i++) Assert.True(JsonElement.DeepEquals(frames[i], result[i].Data));
            Assert.Equal("vertex-3", result[3].EventId); Assert.Equal(600, result[3].RetryMilliseconds); Assert.Equal("message", result[3].EventName);
        }
        else
        {
            var result = await adapter.GenerateNativeAsync(VertexFixture.Request(index), TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(VertexFixture.Part("response"), result.Data));
            var image = Assert.Single(result.Images); Assert.Equal("signed-image==", image.Data.GetProperty("thoughtSignature").GetString()); Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray()); Assert.False(image.Data.TryGetProperty("b64_json", out _));
            Assert.Equal(options.ModelResource, result.Metadata.UpstreamModel); Assert.Equal("pictures", result.Metadata.PublicModel);
        }
        Assert.Equal(key ? 0 : 1, source.Calls); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>现有公共 Image 路由使用完整云目标，不在正文加入路径模型。</summary>
    [Fact]
    public async Task PublicPromptRoutePreservesNativeSettings()
    {
        using var factory = new VertexTestFactory(); factory.OnSend = async (message, token) =>
        {
            var body = VertexFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.False(body.TryGetProperty("model", out _));
            Assert.Equal("a cat", body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.True(JsonElement.DeepEquals(VertexFixture.Request().Parameters["generationConfig"]!.Deserialize<JsonElement>(), body.GetProperty("generationConfig")));
            return VertexFixture.Json(VertexFixture.Part("response"));
        };
        await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true))] }] }).Build();
        var result = await client.Image.GenerateAsync(new() { Model = "pictures", Prompt = "a cat", Parameters = new() { ["generationConfig"] = VertexFixture.Part("transports")[0].GetProperty("request").GetProperty("generationConfig") } }, TestContext.Current.CancellationToken); Assert.Single(result.Images);
    }
    /// <summary>原生安全过滤和无图片结果保持完整可访问，而不是伪造图像或 API 错误。</summary>
    [Theory]
    [InlineData("blocked")] [InlineData("filtered")]
    public async Task SafetyOutcomeIsNative(string name)
    {
        using var factory = new VertexTestFactory { OnSend = (_, _) => Task.FromResult(VertexFixture.Json(VertexFixture.Part(name))) };
        var result = await new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true)).GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(VertexFixture.Part(name), result.Data));
    }
    /// <summary>认证等待前固定完整请求，同主体短期令牌值可轮换而不借用其他 Key。</summary>
    [Fact]
    public async Task RequestSnapshotPrecedesAuthenticationAndTokenRotationIsExplicit()
    {
        using var factory = new VertexTestFactory(); using var source = new VertexTestTokenSource(); var gate = new TaskCompletionSource<GoogleVertexImageAccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OnGet = _ => new(gate.Task); var adapter = new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), new VertexTestTime()); var request = VertexFixture.Request();
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(VertexFixture.Part("transports")[0].GetProperty("request"), VertexFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return VertexFixture.Json(VertexFixture.Part("response")); };
        var call = adapter.GenerateNativeAsync(request, TestContext.Current.CancellationToken); request.Parameters.Clear(); Assert.Equal(0, factory.Calls);
        gate.SetResult(new("fixture-principal", "fixture-token-one", new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))); await call;
        source.OnGet = _ => ValueTask.FromResult(new GoogleVertexImageAccessToken("fixture-principal", "fixture-token-two", new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        factory.OnSend = (message, _) => { Assert.Equal("fixture-token-two", message.Headers.Authorization!.Parameter); return Task.FromResult(VertexFixture.Json(VertexFixture.Part("response"))); };
        await adapter.GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken); Assert.Equal(2, source.Calls); Assert.Equal(2, factory.Calls);
    }
    /// <summary>流请求枚举前冻结且只能使用一次，不进行重连或重放。</summary>
    [Fact]
    public async Task StreamSnapshotIsSingleUse()
    {
        using var factory = new VertexTestFactory { OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(VertexFixture.Part("transports")[0].GetProperty("request"), VertexFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return VertexFixture.Sse(VertexFixture.Bytes("vertex-images.sse")); } };
        var request = VertexFixture.Request(); var stream = new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true)).StreamNativeAsync(request, TestContext.Current.CancellationToken); request.Parameters.Clear();
        Assert.Equal(4, (await VertexFixture.Collect(stream)).Count); await Assert.ThrowsAsync<InvalidOperationException>(() => VertexFixture.Collect(stream)); Assert.Equal(1, factory.Calls);
    }
}
