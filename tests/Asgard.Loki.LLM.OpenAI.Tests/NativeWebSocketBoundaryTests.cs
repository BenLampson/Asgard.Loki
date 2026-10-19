using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>资源策略、供应商绑定身份和未来事件边界。</summary>
public sealed class NativeWebSocketBoundaryTests
{
    /// <summary>资源策略必须在初始化时有效。</summary>
    [Theory]
    [InlineData(0, 1, 1)] [InlineData(1, 0, 1)] [InlineData(1, 1, 0)]
    public void InvalidLimits_FailAtInitialization(int bytes, int pending, int tracked)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiLlmProvider(new StubHttpClientFactory(handler), new OpenAiResponsesWebSocketOptions
        { MaximumMessageBytes = bytes, MaximumPendingRequests = pending, MaximumTrackedResponses = tracked }));
    }

    /// <summary>待处理请求有独立上限；本地拒绝不会偷偷生成请求。</summary>
    [Fact]
    public async Task PendingLimit_RejectsBeforeSending()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory, new() { MaximumPendingRequests = 1 });
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(Create("other"), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Sockets.Single().SendAttempts);
    }

    /// <summary>响应身份缓存有上限，不能无限积累生命周期状态。</summary>
    [Fact]
    public async Task ResponseIdentityLimit_FailsExplicitly()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory, new() { MaximumTrackedResponses = 1 });
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("one"), TestContext.Current.CancellationToken); await connection.SendAsync(Create("two"), TestContext.Current.CancellationToken);
        var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "a", "one")); socket.Text(Lifecycle("response.created", "b", "two"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(connection, TestContext.Current.CancellationToken)); Assert.Equal(1, socket.Disposals);
    }

    /// <summary>命名前缀相似的未来事件依然完整保留，不受已知输出事件状态校验误伤。</summary>
    [Fact]
    public async Task UnknownNativeEvents_ArePreservedBeforeAndAfterResponse()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create(), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single();
        const string future = """{"type":"response.output_future.audit","payload":{"signed":"opaque","future":false}}""";
        socket.Text(future); socket.Text(Lifecycle("response.created")); socket.Text(Lifecycle("response.completed")); socket.Text(future); socket.PeerClose();
        var events = await Drain(connection, TestContext.Current.CancellationToken); Assert.Equal(future, events[0].GetRawText()); Assert.Equal(future, events[3].GetRawText());
    }

    /// <summary>自定义连接工厂的配置身份变化立即使旧连接无效，不能沿用旧会话偷偷换传输语义。</summary>
    [Fact]
    public async Task ChangedFactoryIdentity_InvalidatesExistingBinding()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        factory.Version = "2";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(Create(), TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Sockets.Single().SendAttempts);
    }

    /// <summary>开始生成后 event_id 仍受关联约束，不能复用到另一提交造成错误归属混淆。</summary>
    [Fact]
    public async Task ActiveEventId_CannotBeReused()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main", "same"), TestContext.Current.CancellationToken);
        factory.Sockets.Single().Text(Lifecycle("response.created", lane: "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(Create("other", "same"), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Sockets.Single().SendAttempts);
    }
}
