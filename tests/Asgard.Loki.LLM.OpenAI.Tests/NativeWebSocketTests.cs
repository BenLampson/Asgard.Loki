using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>公开 SDK、完整目标握手及原生多流调用的离线验证。</summary>
public sealed class NativeWebSocketTests
{
    /// <summary>从固定完整目标构造协议和地址，不继承另一个请求的认证头。</summary>
    [Theory]
    [InlineData("https://a.invalid/custom/v1/", "wss://a.invalid/custom/v1/responses")]
    [InlineData("http://a.invalid:8080/custom/v1", "ws://a.invalid:8080/custom/v1/responses")]
    public async Task Open_UsesWholeTargetAndOwnedSocket(string baseAddress, string expected)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory, targets: [Target() with { BaseAddress = new(baseAddress) }]);
        var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var handshake = Assert.Single(factory.Calls); Assert.Equal(expected, handshake.Address.AbsoluteUri);
        Assert.Equal("Bearer test-key-a", handshake.Headers["Authorization"]); Assert.Single(handshake.Headers); Assert.Equal("actual-a", connection.Metadata.UpstreamModel);
        await connection.DisposeAsync(); await connection.DisposeAsync(); Assert.Equal(1, factory.Sockets.Single().Disposals); Assert.Equal(0, factory.Disposals);
    }

    /// <summary>同连接的并行命名流可交错，工具调用、签名、用量与未知字段完整透传。</summary>
    [Fact]
    public async Task ParallelLanes_PreserveNativeTranscriptAndResponseOwnership()
    {
        var factory = new StubResponsesWebSocketFactory(); var store = CompactionFixture.Store(); await using var client = Build(factory, store: store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var first = Create("one"); first["store"] = false; first["generate"] = false;
        first["input"] = JsonNode.Parse("""[{"type":"compaction","encrypted_content":"opaque","future":null},{"type":"function_call_output","call_id":"call_x","output":"ok"}]""");
        var before = first.ToJsonString(); await connection.SendAsync(first, TestContext.Current.CancellationToken); await connection.SendAsync(Create("two"), TestContext.Current.CancellationToken);
        Assert.Equal(before, first.ToJsonString()); var socket = factory.Sockets.Single(); using var sent = JsonDocument.Parse(socket.Sent.First().Bytes);
        Assert.Equal("actual-a", sent.RootElement.GetProperty("model").GetString()); Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("opaque", sent.RootElement.GetProperty("input")[0].GetProperty("encrypted_content").GetString());
        var transcript = new[] { Lifecycle("response.created", "resp_a", "one", 0), Lifecycle("response.created", "resp_b", "two", 0),
            """{"type":"response.output_text.delta","stream_id":"two","sequence_number":1,"item_id":"msg_b","delta":"你好","future":{"keep":false}}""",
            """{"type":"vendor.future","stream_id":"one","signature":"opaque-signed-value","usage":{"tokens":0}}""",
            Lifecycle("response.completed", "resp_a", "one", 1), Lifecycle("response.failed", "resp_b", "two", 2) };
        foreach (var value in transcript) socket.Text(value, 1); socket.PeerClose();
        var events = await Drain(connection, TestContext.Current.CancellationToken); Assert.Equal(transcript, events.Select(value => value.GetRawText()));
        var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.True(await store.OwnsResponseAsync(binding, "resp_a", TestContext.Current.CancellationToken)); Assert.True(await store.OwnsResponseAsync(binding, "resp_b", TestContext.Current.CancellationToken));
    }

    /// <summary>同 lane 的请求按 FIFO 接受；queued ID 不能占用另一个活动生成。</summary>
    [Fact]
    public async Task SameLane_QueuedRequestsRemainDistinct()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken);
        var socket = factory.Sockets.Single();
        foreach (var value in new[] { Lifecycle("response.created", "a", "main", 0), Lifecycle("response.queued", "b", "main", 0),
            Lifecycle("response.completed", "a", "main", 1), Lifecycle("response.created", "b", "main", 1), Lifecycle("response.in_progress", "b", "main", 2), Lifecycle("response.completed", "b", "main", 3) }) socket.Text(value);
        socket.PeerClose(); Assert.Equal(6, (await Drain(connection, TestContext.Current.CancellationToken)).Count);
    }

    /// <summary>握手失败不重试或切换到另一个服务器。</summary>
    [Fact]
    public async Task HandshakeFailure_DoesNotRetryOrFailOver()
    {
        var factory = new StubResponsesWebSocketFactory { Connect = _ => throw new WebSocketException("handshake failed") };
        await using var client = Build(factory, targets: [Target("a"), Target("b")], selector: new TestTargetSelector(1));
        await Assert.ThrowsAsync<WebSocketException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Equal("b.invalid", Assert.Single(factory.Calls).Address.Host); Assert.Empty(factory.Sockets);
    }

    /// <summary>默认构造函数保持二进制签名和既有基础目标的指纹身份。</summary>
    [Fact]
    public void DefaultBindingIdentity_PreservesLegacyConstructor()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        ILlmProvider provider = new OpenAiLlmProvider(new StubHttpClientFactory(handler));
        Assert.Equal(provider.GetType().AssemblyQualifiedName, provider.BindingIdentity);
        Assert.NotNull(typeof(OpenAiLlmProvider).GetConstructor([typeof(IHttpClientFactory), typeof(string), typeof(string)]));
    }
}
