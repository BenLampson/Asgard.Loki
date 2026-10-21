using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>通过公开 SDK 验证原生全窗口、请求保真和显式关闭握手，所有连接均为替身。</summary>
public sealed class SparkNativeTests
{
    /// <summary>完整/流式保持所有原生消息，不累计用量、只取第一项或改变 system 角色。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FullGoldenWindowMatchesNativeRequestAndAllMessages(bool stream)
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory { Socket = SparkSocket.Transcript() }; var clock = new SparkClock(); var provider = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: clock); var request = SparkFixture.Request(); var original = JsonSerializer.Serialize(request); var store = SparkFixture.Store();
        await using var client = SparkFixture.Build(provider, store); var expected = SparkFixture.Part("frames").EnumerateArray().ToArray(); List<SparkEvent> events = [];
        if (stream) await foreach (var item in client.LLM.Spark.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken))
        { Assert.Equal(events.Count < 3 ? 0 : 1, factory.Socket.Disposals); events.Add(item); }
        else events.AddRange((await client.LLM.Spark.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken)).Events);
        Assert.Equal(expected.Length, events.Count); for (var i = 0; i < expected.Length; i++) { Assert.True(JsonElement.DeepEquals(expected[i], events[i].Data)); Assert.Equal(SparkFixture.Transcript()[i], events[i].Data.GetRawText()); Assert.Null(events[i].Id); Assert.Equal(new LlmResultMetadata("public-spark", "lite", "xunfei", "native-target"), events[i].Metadata); }
        Assert.True(JsonElement.DeepEquals(SparkFixture.Part("wire"), JsonSerializer.Deserialize<JsonElement>(factory.Socket.Sent!))); Assert.Equal(original, JsonSerializer.Serialize(request)); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Socket.Sends); Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, factory.Socket.SentType); Assert.True(factory.Socket.SentEnd); Assert.Equal(1, factory.Socket.CloseOutputs); Assert.Equal(0, factory.Socket.CloseCalls); Assert.Equal(1, factory.Socket.Disposals); Assert.False(source.Disposed); Assert.False(factory.Disposed); Assert.All(clock.Timers, t => Assert.True(t.Disposed));
        var binding = await store.GetAsync("native", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.False(await store.OwnsResponseAsync(binding, "spark-native-sid", TestContext.Current.CancellationToken));
    }
    /// <summary>文本先结束、会话后结束时，原生用量/插件元数据与两个结束信号一起延迟交付。</summary>
    [Fact]
    public async Task DistinctTextAndGlobalTerminalKeepCompleteTail()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory { Socket = SparkSocket.Native("split_terminal_frames") }; var provider = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()); var expected = SparkFixture.Part("split_terminal_frames").EnumerateArray().ToArray(); var count = 0;
        await foreach (var item in provider.StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) { Assert.True(JsonElement.DeepEquals(expected[count], item.Data)); Assert.Equal(count < 3 ? 0 : 1, factory.Socket.Disposals); count++; } Assert.Equal(5, count); Assert.Equal(1, factory.Socket.CloseOutputs);
    }
    /// <summary>原生请求在真正等待凭据前冻结，等待期间修改 DOM 不改变实际发送字节。</summary>
    [Fact]
    public async Task PayloadSnapshotPrecedesCredentialAwait()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.Acquire = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return source.Credentials; }; var body = SparkFixture.Body();
        var call = SparkFixture.ReadAsync(new(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()), TestContext.Current.CancellationToken, body); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); body.Clear(); release.SetResult(); Assert.Equal(4, (await call).Count); Assert.True(JsonElement.DeepEquals(SparkFixture.Part("wire"), JsonSerializer.Deserialize<JsonElement>(factory.Socket.Sent!)));
    }
    /// <summary>提前停止流会释放操作拥有的 socket，不重放、不释放借用来源或工厂。</summary>
    [Fact]
    public async Task EarlyConsumerBreakDisposesOnlyOwnedSocket()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); await foreach (var _ in new XunfeiSparkProvider(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()).StreamAsync(SparkFixture.Body(), SparkFixture.Context(), TestContext.Current.CancellationToken)) break;
        Assert.Equal(1, factory.Socket.Disposals); Assert.Equal(0, factory.Socket.CloseOutputs); Assert.Equal(1, factory.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
}
