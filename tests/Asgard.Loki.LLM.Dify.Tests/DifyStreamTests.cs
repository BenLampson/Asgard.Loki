using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Dify.Tests.DifyFixture;
using static Asgard.Loki.LLM.Dify.Tests.DifyStreamFixture;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>原生三类应用的完整流与独立窗口比较，生成结束不丢失工作流或 TTS 尾部。</summary>
public sealed class DifyStreamTests
{
    /// <summary>逐字节传输的完整 JSON、独立原生标识和传输身份无损，真实终态在 EOF/全部关闭之后可见。</summary>
    [Theory] [InlineData("chatbot", DifyChatProfile.Chatbot, 2)] [InlineData("agent", DifyChatProfile.Agent, 5)] [InlineData("chatflow", DifyChatProfile.Chatflow, 5)]
    public async Task FullNativeStreams_MatchIndependentWindows(string name, DifyChatProfile profile, int terminal)
    {
        var body = new DifyBodyStream(Encoding.UTF8.GetBytes(Text(name + ".sse"))); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Part("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Token, message.Headers.Authorization!.Parameter);
            Assert.True(JsonElement.DeepEquals(Part("wire_streaming"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        using var factory = new DifyHttpFactory(handler); var client = new DifyChatClient(factory, Options(profile), Token); var request = Request(); var before = JsonSerializer.Serialize(request); List<DifyChatEvent> events = [];
        await foreach (var item in client.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            if (item.IsTerminal || item.Json.GetProperty("event").GetString() == "message_end") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            events.Add(item);
        }
        var expected = Frames(name); Assert.Equal(expected.Count, events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.Equal(expected[i], events[i].Data); Assert.Equal(expected[i], events[i].Json.GetRawText()); Assert.Equal(i == terminal, events[i].IsTerminal); Assert.Equal(client.Metadata, events[i].Metadata);
            Assert.Equal(name == "chatbot" ? "transport-a" : null, events[i].EventId); Assert.Equal(name == "chatbot" ? 0L : (long?)null, events[i].RetryMilliseconds);
            Assert.Equal(name == "chatbot" && i == 0 ? "transport-name" : null, events[i].EventName);
        }
        if (name == "agent") { Assert.Equal("thought-a", events[0].Json.GetProperty("id").GetString()); Assert.Equal("完整回答", events[3].Json.GetProperty("answer").GetString()); Assert.Equal(events[3].Json.GetProperty("answer").GetString(), events[4].Json.GetProperty("answer").GetString()); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>原生失败/停止/暂停保持原始状态而非伪造成功、轮询、表单提交或恢复。</summary>
    [Theory] [InlineData("failed")] [InlineData("stopped")] [InlineData("paused")]
    public async Task NativeNonSuccessOutcomes_ArePreserved(string outcome)
    {
        var frames = Frames(); var finish = outcome == "paused"
            ? "{\"event\":\"workflow_paused\",\"task_id\":\"task-a\",\"message_id\":\"msg-a\",\"conversation_id\":\"conv/+&\",\"workflow_run_id\":\"run-a\",\"data\":{\"workflow_run_id\":\"run-a\",\"status\":\"paused\",\"reasons\":[{\"form_id\":\"form-a\",\"unknown\":null}]}}"
            : frames[5].Replace("succeeded", outcome, StringComparison.Ordinal);
        frames = [frames[0], finish]; using var handler = Reply(new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))), "text/event-stream"); using var factory = new DifyHttpFactory(handler);
        var events = await Collect(new(factory, Options(DifyChatProfile.Chatflow), Token), TestContext.Current.CancellationToken); Assert.Equal(2, events.Count); Assert.Equal(finish, events[1].Data); Assert.True(events[1].IsTerminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>失败后的原生 error 必须保持原始数据，不能先交付失败终态再吞掉错误。</summary>
    [Theory] [InlineData("before")] [InlineData("message-end")] [InlineData("workflow-end")] [InlineData("workflow-failed")]
    public async Task NativeError_HasRawBodyBeforeOrAfterTerminal(string position)
    {
        const string error = "{\"event\":\"error\",\"code\":\"provider_error\",\"status\":500,\"message\":\"原生失败\",\"unknown\":{\"exact\":9007199254740993}}";
        var frames = Frames(); frames = position switch { "before" => [], "message-end" => frames.Take(5).ToList(), _ => frames.Take(6).ToList() };
        if (position == "workflow-failed") frames[5] = frames[5].Replace("succeeded", "failed", StringComparison.Ordinal);
        frames.Add(error); using var handler = Reply(new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))), "text/event-stream"); using var factory = new DifyHttpFactory(handler); List<DifyChatEvent> delivered = [];
        var exception = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in new DifyChatClient(factory, Options(DifyChatProfile.Chatflow), Token).StreamAsync(Request(), TestContext.Current.CancellationToken)) delivered.Add(item); });
        Assert.Equal(error, exception.ResponseBody); Assert.Equal(HttpStatusCode.OK, exception.StatusCode); Assert.DoesNotContain(delivered, x => x.IsTerminal || x.Json.GetProperty("event").GetString() == "message_end"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同值的节点/消息/思考/任务标识不合并域，不透明工具内容中的反向关联保持原文。</summary>
    [Fact]
    public async Task EqualValuesAcrossIndependentDomains_AreNotCollapsed()
    {
        var frames = Frames("agent"); for (var i = 0; i < frames.Count; i++) Change(frames, i, "id", "task-a");
        using var handler = Reply(new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))), "text/event-stream"); using var factory = new DifyHttpFactory(handler);
        var events = await Collect(new(factory, Options(DifyChatProfile.Agent), Token), TestContext.Current.CancellationToken); Assert.Equal(6, events.Count); Assert.True(events[^1].IsTerminal);
        Assert.Contains("call-b", events[0].Json.GetProperty("tool_input").GetString()); Assert.Contains("call-a", events[1].Json.GetProperty("tool_input").GetString());
    }
    /// <summary>流请求快照在工厂变更之前建立；提前结束枚举仍关闭拥有的资源且不读取终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamSnapshotAndEarlyExit_HonorOwnership(bool early)
    {
        var request = Request(); var body = new DifyBodyStream(Encoding.UTF8.GetBytes(Text("chatbot.sse"))); using var handler = new StubHttpHandler(async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(Part("wire_streaming"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return response; });
        using var factory = new DifyHttpFactory(handler) { Configure = _ => request.Parameters.Clear() }; var client = new DifyChatClient(factory, Options(), Token);
        if (early) { await foreach (var item in client.StreamAsync(request, TestContext.Current.CancellationToken)) { Assert.False(item.IsTerminal); break; } Assert.False(body.Eof); }
        else Assert.Equal(6, (await Collect(client, TestContext.Current.CancellationToken, request)).Count);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Empty(request.Parameters); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生审核/注释直答没有运行身份，message_end 仍是实际终态；不补造工作流。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChatflowDirectAnswerWithoutRun_IsPreserved(bool withMessage)
    {
        var original = Frames("chatbot"); List<string> frames = withMessage ? [original[0], original[2], original[3], original[4]] : [original[2]];
        var body = new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))); using var handler = Reply(body, "text/event-stream"); using var factory = new DifyHttpFactory(handler); List<DifyChatEvent> events = [];
        await foreach (var item in new DifyChatClient(factory, Options(DifyChatProfile.Chatflow), Token).StreamAsync(Request(), TestContext.Current.CancellationToken))
        { if (item.IsTerminal) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } events.Add(item); }
        Assert.Equal(frames, events.Select(x => x.Data)); Assert.Single(events, x => x.IsTerminal); Assert.DoesNotContain(events, x => x.Json.TryGetProperty("workflow_run_id", out _)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生停止顺序可以先 workflow_finished 再 message_end，全部尾帧仍保持原序。</summary>
    [Fact]
    public async Task StoppedWorkflowBeforeMessageEnd_RetainsNativeOrder()
    {
        var original = Frames(); List<string> frames = [original[0], original[5].Replace("succeeded", "stopped", StringComparison.Ordinal), original[4], original[6]];
        using var handler = Reply(new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))), "text/event-stream"); using var factory = new DifyHttpFactory(handler);
        var events = await Collect(new(factory, Options(DifyChatProfile.Chatflow), Token), TestContext.Current.CancellationToken);
        Assert.Equal(frames, events.Select(x => x.Data)); Assert.True(events[1].IsTerminal); Assert.False(events[2].IsTerminal); Assert.Equal(1, handler.Calls);
    }

}
