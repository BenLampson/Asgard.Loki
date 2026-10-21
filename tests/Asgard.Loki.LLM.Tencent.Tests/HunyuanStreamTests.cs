using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>独立原生 SSE 窗口、完整流式签名和多选择黄金测试，完全离线。</summary>
public sealed class HunyuanStreamTests
{
    public static IEnumerable<object[]> StreamVectors() => HunyuanFixture.Part("vectors", "tc3.json").EnumerateArray().Where(v => v.GetProperty("stream").GetBoolean()).Select(v => new object[] { v.GetRawText() });
    /// <summary>原生事件、工具分片、推理、尾部和 SSE 元数据与独立样本逐字段比较。</summary>
    [Fact]
    public async Task GoldenNativeStreamRetainsEveryFieldAndClosesBeforeTerminalDelivery()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var stream = new HunyuanReadStream(Encoding.UTF8.GetBytes(HunyuanFixture.NativeSse())); var request = HunyuanFixture.Request(); var original = JsonSerializer.Serialize(request);
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("wire_stream"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return response; };
        await using var client = HunyuanFixture.Build(new(factory, source, timeProvider: new HunyuanClock())); var golden = HunyuanFixture.Part("events").EnumerateArray().ToArray(); var count = 0;
        await foreach (var item in client.LLM.Hunyuan.StreamAsync(request, new() { SessionId = "stream" }, TestContext.Current.CancellationToken))
        {
            Assert.True(JsonElement.DeepEquals(golden[count], item.Data)); Assert.Equal(count < 4 ? "native-0" : "native-tail", item.EventId); Assert.Equal(count == 0 ? "native.delta" : count == 4 ? "native.recommendations" : null, item.EventName); Assert.Equal(600, item.RetryMilliseconds);
            Assert.Equal(new LlmResultMetadata("public", "native-fixed", "tencent", "native-target"), item.Metadata); Assert.Null(item.Id);
            if (count >= 3) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); } else Assert.False(stream.Eof); count++;
        }
        Assert.Equal(6, count); Assert.Equal(original, JsonSerializer.Serialize(request)); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>四个独立 true-body 签名向量经实际流式 HTTP 请求验证，正文不重序列化为另一种结构。</summary>
    [Theory] [MemberData(nameof(StreamVectors))]
    public async Task StreamWireMatchesIndependentTc3Vector(string json)
    {
        var vector = JsonSerializer.Deserialize<JsonElement>(json); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var endpoint = vector.GetProperty("endpoint").GetString()!;
        factory.OnSend = async (message, token) => { Assert.Equal(vector.GetProperty("body").GetString(), await message.Content!.ReadAsStringAsync(token)); Assert.Equal(vector.GetProperty("authorization").GetString(), message.Headers.GetValues("Authorization").Single()); Assert.Equal(vector.GetProperty("host").GetString(), message.Headers.Host); Assert.Equal("text/event-stream", message.Headers.Accept.Single().MediaType); Assert.Equal("application/json", message.Content.Headers.ContentType!.ToString()); return HunyuanFixture.Sse(); };
        var provider = new TencentHunyuanProvider(factory, source, new() { SigningHost = vector.GetProperty("signing_host").GetString() }, timeProvider: new HunyuanClock());
        Assert.Equal(6, (await HunyuanFixture.ReadAsync(provider, TestContext.Current.CancellationToken, JsonNode.Parse(vector.GetProperty("body").GetString()!)!.AsObject(), HunyuanFixture.Context(endpoint))).Count); Assert.Equal(1, factory.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>首个选择结束后其他选择仍能继续生成，所有原生顺序和稀疏索引保持。</summary>
    [Fact]
    public async Task AllIndexedChoicesFinishBeforeCompleteTailIsReleased()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var golden = HunyuanFixture.Part("indexed_events").EnumerateArray().ToArray(); var stream = new HunyuanReadStream(Encoding.UTF8.GetBytes(HunyuanFixture.Frames(golden)));
        factory.OnSend = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response); }; var provider = new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()); var count = 0;
        await foreach (var item in provider.StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)) { Assert.True(JsonElement.DeepEquals(golden[count], item.Data)); if (count > 0) { Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); } count++; } Assert.Equal(5, count);
    }
    /// <summary>原生空生成、稀疏极大索引及未知结束原因保持原样，不补模型/索引或改成 stop。</summary>
    [Theory] [InlineData("stop", false)] [InlineData("sensitive", false)] [InlineData("tool_calls", false)] [InlineData("future_reason", true)]
    public async Task EmptyNativeGenerationAndFutureFinishRemainNative(string finish, bool sparse)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var choice = new JsonObject { ["Delta"] = new JsonObject { ["Content"] = "" }, ["FinishReason"] = finish }; if (sparse) choice["Index"] = int.MaxValue; var data = JsonSerializer.SerializeToElement(new JsonObject { ["Id"] = "native-empty", ["Choices"] = new JsonArray(choice), ["Future"] = false }); factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Sse(HunyuanFixture.Frames([data])));
        var provider = new TencentHunyuanProvider(factory, source, new() { MaximumTrackedChoices = 1 }, timeProvider: new HunyuanClock()); Assert.True(JsonElement.DeepEquals(data, Assert.Single(await HunyuanFixture.ReadAsync(provider, TestContext.Current.CancellationToken)).Data));
    }
    /// <summary>流式请求也在真正的凭据等待之前冻结，之后的对象变化不能改变签名正文。</summary>
    [Fact]
    public async Task StreamSnapshotPrecedesCredentialAwait()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.Acquire = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return source.Credentials; };
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("wire_stream"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return HunyuanFixture.Sse(); }; var body = HunyuanFixture.StreamBody(); var call = HunyuanFixture.ReadAsync(new(factory, source, timeProvider: new HunyuanClock()), TestContext.Current.CancellationToken, body); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); body.Clear(); release.SetResult(); Assert.Equal(6, (await call).Count);
    }
    /// <summary>流式和完整原生入口共享同一完整绑定，稳定逻辑主体允许 Key 轮换。</summary>
    [Fact]
    public async Task JsonAndStreamKeepSameBoundTargetAcrossCredentialRotation()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var store = HunyuanFixture.Store(); await using var client = HunyuanFixture.Build(new(factory, source, timeProvider: new HunyuanClock()), store); var options = new LlmCallOptions { SessionId = "bound" };
        await client.LLM.Hunyuan.CompleteAsync(HunyuanFixture.Request(), options, TestContext.Current.CancellationToken); source.Credentials = new("fixture-tencent-principal", "AKIDROTATED", "rotated-fixture", DateTimeOffset.MaxValue, "rotated-token"); factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Sse()); var count = 0;
        await foreach (var _ in client.LLM.Hunyuan.StreamAsync(HunyuanFixture.Request(), options, TestContext.Current.CancellationToken)) count++;
        Assert.Equal(6, count); Assert.Equal(2, source.Calls); Assert.Equal(2, factory.Calls); var binding = await store.GetAsync("bound", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal("native-target", binding.TargetId); Assert.False(await store.OwnsResponseAsync(binding, "hunyuan-stream", TestContext.Current.CancellationToken));
    }
    /// <summary>默认新增流限制不改变既有完整调用绑定字节；显式限制进入身份。</summary>
    [Fact]
    public void DefaultStreamPoliciesPreserveExistingIdentitySerialization()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var provider = new TencentHunyuanProvider(factory, source);
        Assert.EndsWith(":hunyuan-tc3-v1:{\"Options\":{\"Region\":null,\"SigningHost\":null,\"MaximumRequestBytes\":1048576,\"MaximumResponseBytes\":33554432},\"Client\":\"Asgard.Loki.LLM.Tencent\",\"Source\":\"fixture-source\"}", provider.BindingIdentity);
        Assert.NotEqual(provider.BindingIdentity, new TencentHunyuanProvider(factory, source, new() { MaximumBufferedTerminalEvents = 128 }).BindingIdentity); Assert.NotEqual(provider.BindingIdentity, new TencentHunyuanProvider(factory, source, new() { MaximumTrackedChoices = 64 }).BindingIdentity);
    }
    /// <summary>null 非终态与嵌套未知业务字段保持，不把业务 FinishReason/Content 当协议控制字段。</summary>
    [Fact]
    public async Task NullNonterminalAndUnknownTailStayOpaque()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var nodes = JsonNode.Parse(HunyuanFixture.Part("events").GetRawText())!.AsArray(); nodes[0]!["Choices"]![0]!["FinishReason"] = null;
        nodes[4]!["Future"] = new JsonObject { ["Choices"] = new JsonArray(new JsonObject { ["FinishReason"] = "business", ["Delta"] = new JsonObject { ["Content"] = "opaque" } }), ["Exact"] = 9007199254740993L };
        var golden = nodes.Select(n => JsonSerializer.SerializeToElement(n)).ToArray(); factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Sse(HunyuanFixture.Frames(golden))); var actual = await HunyuanFixture.ReadAsync(new(factory, source, timeProvider: new HunyuanClock()), TestContext.Current.CancellationToken);
        Assert.Equal(golden.Length, actual.Count); for (var i = 0; i < golden.Length; i++) Assert.True(JsonElement.DeepEquals(golden[i], actual[i].Data));
    }
}
