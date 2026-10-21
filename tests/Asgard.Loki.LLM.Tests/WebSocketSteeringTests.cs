using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>原生 mid-turn steering 也必须尊重完整会话的响应归属。</summary>
public sealed class WebSocketSteeringTests
{
    /// <summary>同会话响应可被显式 steer；另一会话即使使用相同目标也不能借用。</summary>
    [Fact]
    public async Task Steering_RequiresOwnedResponseAndPreservesPayload()
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var owner = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        provider.Opened.First().Connection.Emit("""{"type":"response.created","response":{"id":"resp_active","status":"in_progress"}}""");
        await using (var events = owner.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
            Assert.True(await events.MoveNextAsync());
        await using var other = await client.LLM.Responses.OpenWebSocketAsync("public", Call("other"), TestContext.Current.CancellationToken);
        var input = new JsonObject { ["type"] = "response.steer", ["previous_response_id"] = "resp_active",
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "change priority" }), ["future"] = false };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await other.SendAsync(input, TestContext.Current.CancellationToken));
        await owner.SendAsync(input, TestContext.Current.CancellationToken);
        Assert.Empty(provider.Opened.Last().Connection.Sent);
        var sent = Assert.Single(provider.Opened.First().Connection.Sent); Assert.True(JsonNode.DeepEquals(input, sent));
        Assert.False(sent.ContainsKey("model"));
    }

    /// <summary>steer 的必需引用不能被遗漏、空白或 null 绕过。</summary>
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public async Task Steering_RejectsMissingResponseId(string? previous)
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var message = new JsonObject { ["type"] = "response.steer", ["input"] = "priority" };
        if (previous is not null) message["previous_response_id"] = previous;
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(message, TestContext.Current.CancellationToken));
        Assert.Empty(Assert.Single(provider.Opened).Connection.Sent);
    }
}
