using System.Text;
using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>Chat/Message 双层关联、真实 done 和晚到内容不能互相掩盖。</summary>
public sealed class CozeStreamStateTests
{
    /// <summary>双层状态、消息关联及原生控制帧错误都失败，不能先交付任何 Chat/流终态。</summary>
    [Theory] [InlineData("missing-chat")] [InlineData("missing-done")] [InlineData("duplicate-created")] [InlineData("early-message")]
    [InlineData("missing-message-complete")] [InlineData("duplicate-message-complete")] [InlineData("delta-after-message-complete")]
    [InlineData("changed-chat")] [InlineData("changed-conversation")] [InlineData("changed-bot")] [InlineData("changed-message-chat")]
    [InlineData("changed-message-type")] [InlineData("contradictory-status")] [InlineData("late-message")] [InlineData("duplicate-chat-terminal")]
    [InlineData("duplicate-done")] [InlineData("late-opaque")] [InlineData("wrong-done-payload")] [InlineData("wrong-done-event")]
    [InlineData("known-invalid-json")] [InlineData("known-duplicate-json")] [InlineData("known-invalid-unicode")]
    public async Task InvalidNativeWindow_DoesNotReleaseTerminal(string kind)
    {
        var frames = CozeStreamFixture.Events();
        switch (kind)
        {
            case "missing-chat": frames.RemoveAt(8); break; case "missing-done": frames.RemoveAt(10); break;
            case "duplicate-created": frames.Insert(1, frames[0]); break; case "early-message": frames.RemoveRange(0, 2); break;
            case "missing-message-complete": frames.RemoveAt(5); break; case "duplicate-message-complete": frames.Insert(6, frames[5]); break;
            case "delta-after-message-complete": frames.Insert(6, frames[4]); break;
            case "changed-chat": CozeStreamFixture.Change(frames, 1, "id", "other-chat"); break;
            case "changed-conversation": CozeStreamFixture.Change(frames, 1, "conversation_id", "other-conversation"); break;
            case "changed-bot": CozeStreamFixture.Change(frames, 1, "bot_id", "other-bot"); break;
            case "changed-message-chat": CozeStreamFixture.Change(frames, 4, "chat_id", "other-chat"); break;
            case "changed-message-type": CozeStreamFixture.Change(frames, 5, "type", "follow_up"); break;
            case "contradictory-status": CozeStreamFixture.Change(frames, 8, "status", "in_progress"); break;
            case "late-message": frames.Insert(9, frames[4]); break; case "duplicate-chat-terminal": frames.Insert(9, frames[8]); break;
            case "duplicate-done": frames.Add(frames[^1]); break; case "late-opaque": frames.Add(("future.metadata", "late data")); break;
            case "wrong-done-payload": frames[10] = ("done", "{}"); break; case "wrong-done-event": frames[10] = ("future.done", "[DONE]"); break;
            case "known-invalid-json": frames[4] = (frames[4].Name, "{"); break;
            case "known-duplicate-json": frames[4] = (frames[4].Name, frames[4].Data[..^1] + ",\"x\":1,\"x\":2}"); break;
            case "known-invalid-unicode": frames[4] = (frames[4].Name, frames[4].Data[..^1] + ",\"x\":\"\\ud800\"}"); break;
        }
        var body = new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))); using var handler = Reply(body, "text/event-stream"); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token); List<CozeChatEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.NotNull(failure); Assert.True(failure is InvalidDataException or JsonException, failure.ToString()); Assert.DoesNotContain(seen, e => e.IsChatTerminal || e.IsTerminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知原生错误或资源故障发生于 Chat 终态及 done 前后均不得吞掉原始错误或先交付终态。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task RawErrorEvent_IsPreservedEvenAfterDone(bool afterDone, bool opaque)
    {
        var frames = CozeStreamFixture.Events(); var error = opaque ? "opaque-native-error 🌏" : "{\"code\":701231,\"msg\":\"native error\",\"future\":null}";
        frames.Insert(afterDone ? frames.Count : 9, ("error", error)); using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(CozeStreamFixture.Wire(frames))), "text/event-stream");
        var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); var terminal = false;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) terminal |= item.IsChatTerminal || item.IsTerminal; });
        Assert.Equal(error, failure.ResponseBody); Assert.Equal(System.Net.HttpStatusCode.OK, failure.StatusCode); Assert.Equal("text/event-stream", failure.ContentType); Assert.False(terminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>关联表和保留尾部均有显式上限，不能按用户消息 ID 或原生未来帧无限扩张。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitStateBudget_PreventsTerminalOnOverflow(bool tail)
    {
        var options = tail ? Options() with { MaximumBufferedTailFrames = 2 } : Options() with { MaximumTrackedMessages = 2 };
        using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse"))), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), options, Token); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) terminal |= item.IsChatTerminal || item.IsTerminal; }); Assert.False(terminal);
    }
}
