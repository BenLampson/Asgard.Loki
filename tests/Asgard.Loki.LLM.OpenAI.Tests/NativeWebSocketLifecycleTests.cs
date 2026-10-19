using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生多流断开、响应关联和终态必须由真实事件证实。</summary>
public sealed class NativeWebSocketLifecycleTests
{
    /// <summary>损坏的生命周期不能通过 socket 正常关闭或另一条流完成来伪装成成功。</summary>
    [Theory]
    [InlineData("no-created")] [InlineData("duplicate-created")] [InlineData("duplicate-terminal")]
    [InlineData("late-output")] [InlineData("wrong-id")] [InlineData("wrong-lane")]
    [InlineData("overlap")] [InlineData("backwards-sequence")] [InlineData("wrong-status")]
    [InlineData("queued-only")] [InlineData("queued-completed")] [InlineData("no-events")]
    [InlineData("other-lane-pending")] [InlineData("wrong-lineage")]
    public async Task InvalidLifecycle_FailsClosed(string kind)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single();
        switch (kind)
        {
            case "no-created": socket.Text(Lifecycle("response.completed", lane: "main")); break;
            case "no-events": break;
            case "queued-only": socket.Text(Lifecycle("response.queued", lane: "main")); break;
            case "queued-completed": socket.Text(Lifecycle("response.queued", lane: "main")); socket.Text(Lifecycle("response.completed", lane: "main")); break;
            case "wrong-lineage": socket.Text(Lifecycle("response.created", lane: "main", previous: "foreign")); break;
            default:
                socket.Text(Lifecycle("response.created", lane: "main", sequence: 0));
                switch (kind)
                {
                    case "duplicate-created": socket.Text(Lifecycle("response.created", lane: "main", sequence: 1)); break;
                    case "duplicate-terminal": socket.Text(Lifecycle("response.completed", lane: "main", sequence: 1)); socket.Text(Lifecycle("response.completed", lane: "main", sequence: 2)); break;
                    case "late-output": socket.Text(Lifecycle("response.completed", lane: "main", sequence: 1)); socket.Text("""{"type":"response.output_text.delta","stream_id":"main","delta":"late"}"""); break;
                    case "wrong-id": socket.Text("""{"type":"response.output_text.delta","stream_id":"main","response_id":"other","delta":"wrong"}"""); break;
                    case "wrong-lane": socket.Text(Lifecycle("response.completed", lane: "else")); break;
                    case "overlap": await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); socket.Text(Lifecycle("response.created", "other", "main")); break;
                    case "backwards-sequence": socket.Text(Lifecycle("response.completed", lane: "main", sequence: 0)); break;
                    case "wrong-status": socket.Text("""{"type":"response.completed","stream_id":"main","response":{"id":"resp_1","status":"in_progress"}}"""); break;
                    case "other-lane-pending": await connection.SendAsync(Create("other"), TestContext.Current.CancellationToken); socket.Text(Lifecycle("response.completed", lane: "main", sequence: 1)); break;
                }
                break;
        }
        socket.PeerClose(); await Assert.ThrowsAsync<InvalidDataException>(() => Drain(connection, TestContext.Current.CancellationToken));
        Assert.Equal(1, socket.Disposals);
    }

    /// <summary>显式失败与不完整保留原始类型及原因，均是有效失败终态，不能转换成 completed。</summary>
    [Theory]
    [InlineData("response.failed")] [InlineData("response.incomplete")]
    public async Task FailureTerminal_IsPreserved(string type)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create(), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created"));
        var terminal = JsonNode.Parse(Lifecycle(type))!.AsObject(); terminal["response"]!["error"] = new JsonObject { ["code"] = "native_failure", ["future"] = false };
        socket.Text(terminal.ToJsonString()); socket.PeerClose(); var events = await Drain(connection, TestContext.Current.CancellationToken);
        Assert.Equal(type, events.Last().GetProperty("type").GetString()); Assert.Equal("native_failure", events.Last().GetProperty("response").GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>生成的 event_id 在开始后仍属于原响应，不能让错误消耗后来排队的提交。</summary>
    [Fact]
    public async Task ActiveCreateError_DoesNotConsumeQueuedCreate()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main", "first"), TestContext.Current.CancellationToken);
        var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", lane: "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(Create("main", "second"), TestContext.Current.CancellationToken);
        socket.Text("""{"type":"error","stream_id":"main","event_id":"first","error":{"code":"server_error"}}""");
        socket.Text(Lifecycle("response.failed", lane: "main")); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("error", events.Current.Type); Assert.True(await events.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await events.MoveNextAsync());
    }

    /// <summary>控制请求错误可被显式 event_id 关联，原生成继续直到自己的真实终态。</summary>
    [Fact]
    public async Task ControlError_DoesNotEndActiveGeneration()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", lane: "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.cancel", ["response_id"] = "resp_1", ["event_id"] = "cancel" }, TestContext.Current.CancellationToken);
        socket.Text("""{"type":"error","stream_id":"main","event_id":"cancel","error":{"code":"response_not_active"}}""");
        socket.Text(Lifecycle("response.completed", lane: "main")); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("error", events.Current.Type); Assert.True(await events.MoveNextAsync()); Assert.Equal("response.completed", events.Current.Type); Assert.False(await events.MoveNextAsync());
    }

    /// <summary>一个命名流的请求验证错误不会结束另一个正在生成的流。</summary>
    [Fact]
    public async Task LaneError_PreservesOtherLane()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("bad"), TestContext.Current.CancellationToken); await connection.SendAsync(Create("good"), TestContext.Current.CancellationToken);
        var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", "good", "good"));
        socket.Text("""{"type":"error","stream_id":"bad","error":{"code":"invalid_request_error","future":null}}""");
        socket.Text(Lifecycle("response.completed", "good", "good")); socket.PeerClose();
        var events = await Drain(connection, TestContext.Current.CancellationToken); Assert.Equal(3, events.Count); Assert.Equal("error", events[1].GetProperty("type").GetString());
    }

    /// <summary>父响应完成后才到达的旧控制错误，不能把新排队请求误当成已经结束。</summary>
    [Fact]
    public async Task LateControlError_DoesNotConsumeNextGeneration()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = Build(factory);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.SendAsync(Create("main", "first"), TestContext.Current.CancellationToken); var socket = factory.Sockets.Single(); socket.Text(Lifecycle("response.created", lane: "main"));
        await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.cancel", ["response_id"] = "resp_1", ["event_id"] = "old-control" }, TestContext.Current.CancellationToken);
        socket.Text(Lifecycle("response.completed", lane: "main")); Assert.True(await events.MoveNextAsync());
        await connection.SendAsync(Create("main", "next"), TestContext.Current.CancellationToken);
        socket.Text("""{"type":"error","stream_id":"main","event_id":"old-control","error":{"code":"response_already_completed"}}"""); socket.PeerClose();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("error", events.Current.Type);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await events.MoveNextAsync());
    }
}
