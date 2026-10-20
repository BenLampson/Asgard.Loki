using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>混元完整调用真实 SDK 路由、原生 JSON 保真和异步认证快照测试。</summary>
public sealed class HunyuanCompleteTests
{
    /// <summary>真实公共入口的原生正文、完整信封、工具关联与元数据逐字段比较。</summary>
    [Fact]
    public async Task PublicCallRetainsEntireGoldenWireAndResponse()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var request = HunyuanFixture.Request(); var before = JsonSerializer.Serialize(request);
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("wire_complete"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return HunyuanFixture.Reply(HunyuanFixture.Part("response")); };
        await using var client = HunyuanFixture.Build(new(factory, source, timeProvider: new HunyuanClock()));
        var result = await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "public" }, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("response"), result.Data)); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(new LlmResultMetadata("public", "native-fixed", "tencent", "native-target"), result.Metadata); Assert.Null(result.Id); Assert.Equal(1, factory.Calls);
    }
    /// <summary>稳定 Session 允许同主体临时 Key 轮换，不改变实际服务器、模型和实现。</summary>
    [Fact]
    public async Task RotatingKeysKeepStableCompleteSessionBinding()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var store = HunyuanFixture.Store(); var provider = new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()); var identity = provider.BindingIdentity;
        await using (var client = HunyuanFixture.Build(provider, store))
        {
            await client.LLM.Hunyuan.CompleteAsync(HunyuanFixture.Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
            source.Credentials = new("fixture-tencent-principal", "AKIDSECOND", "second-fixture-secret", DateTimeOffset.MaxValue, "second-token");
            await client.LLM.Hunyuan.CompleteAsync(HunyuanFixture.Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
        }
        var binding = await store.GetAsync("bound", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal("native-target", binding.TargetId); Assert.Equal(identity, provider.BindingIdentity); Assert.Equal(2, source.Calls); Assert.Equal(2, factory.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>原生请求在首次凭据等待前冻结，等待中修改原对象不能改动签名或发送正文。</summary>
    [Fact]
    public async Task NativeSnapshotPrecedesCredentialAwait()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Acquire = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return source.Credentials; }; var body = HunyuanFixture.Body(); var provider = new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock());
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("wire_complete"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return HunyuanFixture.Reply(HunyuanFixture.Part("response")); };
        var call = provider.CompleteAsync(body, HunyuanFixture.Context(), TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); body.Clear(); release.SetResult(); await call; Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>方法、模型和认证覆盖等不合法请求在凭据获取及工厂创建前失败。</summary>
    [Theory] [InlineData("Model")] [InlineData("model")] [InlineData("stream")] [InlineData("Stream")] [InlineData("Messages")] [InlineData("Action")] [InlineData("Token")] [InlineData("SecretKey")] [InlineData("Region")]
    public async Task InvalidPayloadDoesNotAcquireCredentials(string field)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var body = HunyuanFixture.Body(); body[field] = field == "Model" ? JsonValue.Create("different") : field == "Messages" ? new JsonArray() : JsonValue.Create(true);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new TencentHunyuanProvider(factory, source).CompleteAsync(body, HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>实际请求字节超限先于认证和任何 HTTP 创建。</summary>
    [Fact]
    public async Task RequestByteLimitPrecedesSourceAndFactory()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); await Assert.ThrowsAsync<ArgumentException>(() => new TencentHunyuanProvider(factory, source, new() { MaximumRequestBytes = 1 }).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>当前未实现的原生 SSE 明确拒绝，不发送请求或把 JSON 伪装为增量。</summary>
    [Fact]
    public void UnsupportedStreamDoesNotAcquireOrSend()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); Assert.Throws<NotSupportedException>(() => new TencentHunyuanProvider(factory, source).StreamAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
}
