using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>steering 接受不是生成终态；原生自动续接不依赖 SDK 发起额外调用。</summary>
public sealed class NativeWebSocketSteeringTests
{
    /// <summary>自动后继响应不能消耗同 lane 无关的排队创建；所有原生事件都必须观察到。</summary>
    [Fact]
    public async Task AcceptedSteering_TracksAutomaticContinuationAndQueuedCreate()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "new priority" }, TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken);
        var transcript = new[] { Steering("response.steer.accepted"), Lifecycle("response.incomplete", "parent", "main"),
            Lifecycle("response.created", "successor", "main", previous: "parent"), Lifecycle("response.completed", "successor", "main"),
            Lifecycle("response.created", "queued", "main"), Lifecycle("response.completed", "queued", "main") };
        foreach (var item in transcript) socket.Text(item); socket.PeerClose(); var received = new List<string>();
        while (await events.MoveNextAsync()) received.Add(events.Current.Data.GetRawText()); Assert.Equal(transcript, received);
        Assert.Equal(3, socket.SendAttempts); Assert.Equal(1, socket.Sent.Count(item => JsonNode.Parse(item.Bytes)!["type"]!.GetValue<string>() == "response.steer"));
    }

    /// <summary>已接受或等待工具输入的 steering 在关闭时仍是未落实的工作，不能丢弃并报告成功。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SteeringWithoutSuccessor_IsIncomplete(bool pending)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "priority" }, TestContext.Current.CancellationToken);
        socket.Text(Steering("response.steer.accepted")); socket.Text(Lifecycle("response.completed", "parent", "main"));
        if (pending) socket.Text(Steering("response.steer.pending")); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.True(await events.MoveNextAsync()); if (pending) Assert.True(await events.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await events.MoveNextAsync());
    }

    /// <summary>控制失败保留原生数据，并允许父响应按原本终态结束。</summary>
    [Fact]
    public async Task FailedSteering_DoesNotRequireInventedSuccessor()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "priority" }, TestContext.Current.CancellationToken);
        socket.Text(Steering("response.steer.accepted")); socket.Text(Steering("response.steer.failed")); socket.Text(Lifecycle("response.completed", "parent", "main")); socket.PeerClose();
        List<string?> types = []; while (await events.MoveNextAsync()) types.Add(events.Current.Type);
        Assert.Equal(["response.steer.accepted", "response.steer.failed", "response.completed"], types);
    }

    /// <summary>分配 steering ID 之前的原生拒绝合法省略 id，不能被误当成损坏响应。</summary>
    [Fact]
    public async Task RejectedBeforeAcceptance_PreservesMissingSteeringId()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "priority" }, TestContext.Current.CancellationToken);
        const string failure = """{"type":"response.steer.failed","stream_id":"main","steer":{"previous_response_id":"parent","input":"priority"},"error":{"code":"steering_not_supported","message":"unsupported","type":"invalid_request_error"}}""";
        socket.Text(failure); socket.Text(Lifecycle("response.completed", "parent", "main")); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.Equal(failure, events.Current.Data.GetRawText()); Assert.True(await events.MoveNextAsync()); Assert.False(await events.MoveNextAsync());
    }

    /// <summary>同一 parent 的多个已接受更新可以落实到同一个后继响应，不虚构额外生成。</summary>
    [Fact]
    public async Task MultipleAcceptedSteers_CanCommitToOneSuccessor()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "first" }, TestContext.Current.CancellationToken);
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "second" }, TestContext.Current.CancellationToken);
        socket.Text(Steering("response.steer.accepted")); socket.Text(Steering("response.steer.accepted").Replace("steer_1", "steer_2", StringComparison.Ordinal));
        socket.Text(Lifecycle("response.incomplete", "parent", "main")); socket.Text(Lifecycle("response.created", "successor", "main", previous: "parent")); socket.Text(Lifecycle("response.completed", "successor", "main")); socket.PeerClose();
        var count = 0; while (await events.MoveNextAsync()) count++; Assert.Equal(5, count); Assert.Equal(3, socket.SendAttempts);
    }

    /// <summary>等待工具结果时由调用者显式提交一次，保留 call_id；SDK 不执行工具或自动补发。</summary>
    [Fact]
    public async Task PendingRequiredInput_UsesOneExplicitCallerContinuation()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "parent", "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.steer", ["previous_response_id"] = "parent", ["input"] = "priority" }, TestContext.Current.CancellationToken);
        socket.Text(Steering("response.steer.accepted")); socket.Text(Lifecycle("response.completed", "parent", "main"));
        const string pending = """{"type":"response.steer.pending","stream_id":"main","steer":{"id":"steer_1","previous_response_id":"parent"},"reason":"waiting_for_required_input","required_input":[{"type":"function_call_output","call_id":"call_real","name":"lookup"}]}""";
        socket.Text(pending); Assert.True(await events.MoveNextAsync()); Assert.True(await events.MoveNextAsync()); Assert.True(await events.MoveNextAsync()); Assert.Equal(pending, events.Current.Data.GetRawText());
        Assert.Equal(2, socket.SendAttempts);
        var next = Create("main"); next["previous_response_id"] = "parent";
        next["input"] = new JsonArray(new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call_real", ["output"] = "caller result" });
        await connection.SendAsync(next, TestContext.Current.CancellationToken);
        var sent = JsonNode.Parse(socket.Sent.Last().Bytes)!; Assert.Equal("call_real", sent["input"]![0]!["call_id"]!.GetValue<string>());
        socket.Text(Lifecycle("response.created", "successor", "main", previous: "parent")); socket.Text(Lifecycle("response.completed", "successor", "main")); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.True(await events.MoveNextAsync()); Assert.False(await events.MoveNextAsync()); Assert.Equal(3, socket.SendAttempts);
    }

    /// <summary>只生成独立原生控制样本，不为调用者自动发送任何后续输入。</summary>
    private static string Steering(string type) => new JsonObject { ["type"] = type, ["stream_id"] = "main",
        ["steer"] = new JsonObject { ["id"] = "steer_1", ["previous_response_id"] = "parent", ["input"] = "priority" } }.ToJsonString();
}
