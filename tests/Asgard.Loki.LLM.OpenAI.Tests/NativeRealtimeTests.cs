using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.RealtimeFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.CompactionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>验证实际公共 SDK 的 Realtime 原生握手及完整双向事件，不使用网络或 Key。</summary>
public sealed class NativeRealtimeTests
{
    /// <summary>基础地址、模型、凭据和供应商实现均从固定完整目标选择。</summary>
    [Theory]
    [InlineData("https://a.invalid/custom/v1/", "actual-a", "wss://a.invalid/custom/v1/realtime?model=actual-a")]
    [InlineData("http://a.invalid:8080/custom/v1", "model &/?#你好", "ws://a.invalid:8080/custom/v1/realtime?model=model%20%26%2F%3F%23%E4%BD%A0%E5%A5%BD")]
    public async Task Open_UsesBoundTargetAndEscapedModel(string root, string model, string expected)
    {
        var factory = new StubResponsesWebSocketFactory();
        await using var client = RealtimeFixture.Build(factory, targets: [RealtimeFixture.Target() with { BaseAddress = new(root), UpstreamModel = model }]);
        var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var handshake = Assert.Single(factory.Calls); Assert.Equal(expected, handshake.Address.AbsoluteUri);
        Assert.Equal("Bearer test-key-a", handshake.Headers["Authorization"]); Assert.Single(handshake.Headers);
        Assert.Equal(new("public", model, "openai", "a"), connection.Metadata);
        Assert.Empty(Assert.Single(factory.Sockets).Sent);
        await connection.DisposeAsync(); await connection.DisposeAsync();
        Assert.Equal(1, factory.Sockets.Single().Disposals); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>独立完整样本包含工具关联、音频、零值、未知字段、错误及非成功终态，全部原样往返。</summary>
    [Fact]
    public async Task Duplex_PreservesIndependentTranscriptWithoutExecutingOrAggregating()
    {
        var factory = new StubResponsesWebSocketFactory(); var store = Store(); await using var client = RealtimeFixture.Build(factory, store: store);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var transcript = Transcript(); var socket = Assert.Single(factory.Sockets);
        foreach (var value in transcript.GetProperty("client").EnumerateArray())
            await connection.SendAsync(JsonNode.Parse(value.GetRawText())!.AsObject(), TestContext.Current.CancellationToken);
        var sent = socket.Sent.Select(frame => JsonNode.Parse(frame.Bytes)).ToArray(); var expected = transcript.GetProperty("client").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, sent.Length);
        for (var i = 0; i < expected.Length; i++) Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected[i].GetRawText()), sent[i]));
        foreach (var value in transcript.GetProperty("server").EnumerateArray()) socket.Text(value.GetRawText(), 1);
        socket.PeerClose(); var result = await Drain(connection, TestContext.Current.CancellationToken);
        Assert.Equal(transcript.GetProperty("server").EnumerateArray().Select(e => e.GetRawText()), result.Select(e => e.Data.GetRawText()));
        Assert.All(result, e => Assert.Equal(connection.Metadata, e.Metadata));
        Assert.Equal(expected.Length, socket.Sent.Count); // 读取工具事件不触发调用、自动续写或重发。
        var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.False(await store.OwnsResponseAsync(binding, "resp_tool", TestContext.Current.CancellationToken));
    }
    /// <summary>客户端仅重写合法已知模型位置，工具参数中的 model 不被遍历修改。</summary>
    [Fact]
    public async Task Send_SnapshotsAndPreservesBusinessModelFields()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var socket = Assert.Single(factory.Sockets); socket.BeforeSend = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var message = JsonNode.Parse("""{"type":"session.update","session":{"model":"public","tools":[{"parameters":{"model":"business"}}]},"future":false}""")!.AsObject();
        var send = connection.SendAsync(message, TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        message["session"]!["model"] = "changed"; message["future"] = true; release.SetResult(); await send;
        var result = JsonNode.Parse(Assert.Single(socket.Sent).Bytes)!;
        Assert.Equal("actual-a", result["session"]!["model"]!.GetValue<string>()); Assert.False(result["future"]!.GetValue<bool>());
        Assert.Equal("business", result["session"]!["tools"]![0]!["parameters"]!["model"]!.GetValue<string>());
    }
    /// <summary>只有完整目标声明原生 Realtime 时才能调度；不能借用 Responses 工厂或 HTTP。</summary>
    [Fact]
    public async Task ResponsesTarget_DoesNotImplyRealtime()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = WebSocketFixture.Build(factory);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Empty(factory.Calls);
    }
    /// <summary>失败目标不能自动切换、重连或回退 HTTP。</summary>
    [Fact]
    public async Task HandshakeFailure_DoesNotRetryOrFailOver()
    {
        var factory = new StubResponsesWebSocketFactory { Connect = _ => throw new WebSocketException("failed") };
        await using var client = RealtimeFixture.Build(factory, targets: [RealtimeFixture.Target("a"), RealtimeFixture.Target("b")], selector: new TestTargetSelector(1));
        await Assert.ThrowsAsync<WebSocketException>(() => client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Equal("b.invalid", Assert.Single(factory.Calls).Address.Host);
    }
    /// <summary>默认新增策略保留原有构造签名和绑定字节，显式资源策略参与身份。</summary>
    [Fact]
    public void Options_PreserveDefaultBindingAndRejectInvalidLimits()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var http = new StubHttpClientFactory(handler);
        var prior = new OpenAiLlmProvider(http);
        Assert.Equal(prior.BindingIdentity, new OpenAiLlmProvider(http, new OpenAiRealtimeOptions()).BindingIdentity);
        Assert.NotEqual(prior.BindingIdentity, new OpenAiLlmProvider(http, new OpenAiRealtimeOptions { MaximumMessageBytes = 100 }).BindingIdentity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiLlmProvider(http, new OpenAiRealtimeOptions { MaximumMessageBytes = 0 }));
    }
}
