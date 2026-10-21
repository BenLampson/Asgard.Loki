using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Dify.Tests.DifyFixture;
using static Asgard.Loki.LLM.Dify.Tests.DifyStreamFixture;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>流式关联、阶段、边界和资源故障不能提前发布生成终态。</summary>
public sealed class DifyStreamValidationTests
{
    /// <summary>缺失/重复/错序终态、错误 ID 和跨应用事件不靠字段清理或猜测修复。</summary>
    [Theory] [InlineData("empty")] [InlineData("node-only")] [InlineData("missing-message-end")] [InlineData("missing-workflow-end")] [InlineData("missing-start")] [InlineData("duplicate-start")] [InlineData("duplicate-end")] [InlineData("duplicate-finish")] [InlineData("late-message")] [InlineData("late-node")] [InlineData("late-replace")] [InlineData("task")] [InlineData("message")] [InlineData("conversation")] [InlineData("run")] [InlineData("nested-run")] [InlineData("bad-event")] [InlineData("array")] [InlineData("duplicate-json")] [InlineData("unicode")] [InlineData("done")] [InlineData("profile")] [InlineData("status")] [InlineData("answer")] [InlineData("run-without-start")] [InlineData("inline-late-start")] [InlineData("chatbot-no-end")] [InlineData("agent-no-end")] [InlineData("run-on-message")] [InlineData("workflow-id")] [InlineData("node-without-run")]
    public async Task BrokenNativeLifecycle_DoesNotDeliverTerminal(string fault)
    {
        var frames = Frames(); var options = Options(DifyChatProfile.Chatflow);
        switch (fault)
        {
            case "empty": frames = []; break;
            case "node-without-run": frames = [frames[1].Replace("\"workflow_run_id\":\"run-a\",", "", StringComparison.Ordinal), frames[4]]; break;
            case "workflow-id": frames[5] = frames[5].Replace("\"status\":\"succeeded\"", "\"workflow_id\":\"other-flow\",\"status\":\"succeeded\"", StringComparison.Ordinal); break;
            case "run-on-message": frames = [Frames("chatbot")[0], Frames("chatbot")[2]]; Change(frames, 0, "workflow_run_id", "run-a"); break;
            case "run-without-start": frames = frames.Skip(1).Take(4).ToList(); break;
            case "inline-late-start": frames = [Frames("chatbot")[2], frames[0], frames[5]]; break;
            case "chatbot-no-end": frames = Frames("chatbot").Where(x => !x.Contains("end-event", StringComparison.Ordinal)).ToList(); options = Options(); break;
            case "agent-no-end": frames = Frames("agent").Take(5).ToList(); options = Options(DifyChatProfile.Agent); break;
            case "node-only": frames = frames.Take(3).ToList(); break;
            case "missing-message-end": frames.RemoveAt(4); break;
            case "missing-workflow-end": frames.RemoveAt(5); break;
            case "missing-start": frames.RemoveAt(0); break;
            case "duplicate-start": frames.Insert(1, frames[0]); break;
            case "duplicate-end": frames.Insert(5, frames[4]); break;
            case "duplicate-finish": frames.Insert(6, frames[5]); break;
            case "late-message": frames.Add(frames[3]); break;
            case "late-node": frames.Add(frames[1]); break;
            case "late-replace": frames.Add(Frames("chatbot")[1]); break;
            case "task": Change(frames, 5, "task_id", "other"); break;
            case "message": Change(frames, 4, "message_id", "other"); break;
            case "conversation": Change(frames, 4, "conversation_id", "other"); break;
            case "run": Change(frames, 5, "workflow_run_id", "other"); break;
            case "nested-run": frames[5] = frames[5].Replace("\"id\":\"run-a\"", "\"id\":\"other\"", StringComparison.Ordinal); break;
            case "bad-event": frames.Add("{\"event\":false}"); break;
            case "array": frames.Add("[]"); break;
            case "duplicate-json": frames.Add("{\"event\":\"future\",\"event\":\"message_end\"}"); break;
            case "unicode": frames.Add("{\"event\":\"future\",\"x\":\"\\ud800\"}"); break;
            case "done": frames.Add("[DONE]"); break;
            case "profile": options = Options(); break;
            case "status": frames[5] = frames[5].Replace("succeeded", "running", StringComparison.Ordinal); break;
            case "answer": Change(frames, 3, "answer", null); break;
        }
        var body = new DifyBodyStream(Encoding.UTF8.GetBytes(Wire(frames))); using var handler = Reply(body, "text/event-stream"); using var factory = new DifyHttpFactory(handler); List<DifyChatEvent> delivered = [];
        await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var item in new DifyChatClient(factory, options, Token).StreamAsync(Request(), TestContext.Current.CancellationToken)) delivered.Add(item); });
        Assert.DoesNotContain(delivered, x => x.IsTerminal || x.Json.GetProperty("event").GetString() == "message_end"); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>EOF、长度、尾部/字节预算、解码和资源关闭/最终取消全部先于终态交付。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("client")] [InlineData("cancel-eof")] [InlineData("cancel-body")] [InlineData("cancel-client")] [InlineData("late-target")] [InlineData("late-method")] [InlineData("length-short")] [InlineData("length-long")] [InlineData("budget")] [InlineData("tail")] [InlineData("utf8")] [InlineData("truncated")] [InlineData("media")] [InlineData("charset")]
    public async Task TailAndTransportFaults_PreventTerminalDelivery(string fault)
    {
        using var cancellation = new CancellationTokenSource(); var text = Text("chatflow.sse"); if (fault == "truncated") text = text.TrimEnd(); var bytes = Encoding.UTF8.GetBytes(text); if (fault == "utf8") bytes = [..bytes, 0xff];
        var body = new DifyBodyStream(bytes) { FailAtEof = fault == "read", FailDispose = fault == "dispose", OnEof = fault == "cancel-eof" ? cancellation.Cancel : null, OnDispose = fault == "cancel-body" ? cancellation.Cancel : null };
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) =>
        {
            actual = message; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(fault == "media" ? "text/html" : fault == "charset" ? "text/event-stream; charset=utf-16" : "text/event-stream");
            if (fault.StartsWith("length-", StringComparison.Ordinal)) response.Content.Headers.ContentLength = bytes.Length + (fault == "length-long" ? 1 : -1);
            return Task.FromResult(response);
        });
        using var factory = new DifyHttpFactory(handler) { OnClientDispose = () => { if (fault == "cancel-client") cancellation.Cancel(); if (fault == "client") throw new IOException("client close"); if (fault == "late-target") actual!.RequestUri = new("https://other.invalid/chat-messages"); if (fault == "late-method") actual!.Method = HttpMethod.Get; } };
        var options = Options(DifyChatProfile.Chatflow); if (fault == "budget") options = options with { MaximumStreamBytes = bytes.Length - 1 }; if (fault == "tail") options = options with { MaximumBufferedTailFrames = 1 };
        List<DifyChatEvent> delivered = []; await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var item in new DifyChatClient(factory, options, Token).StreamAsync(Request(), cancellation.Token)) delivered.Add(item); });
        Assert.DoesNotContain(delivered, x => x.IsTerminal || x.Json.GetProperty("event").GetString() == "message_end"); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>JSON 错误或错误媒体不能被当作成功 SSE 或自动降级阻塞模式。</summary>
    [Theory] [InlineData(200, true)] [InlineData(400, true)] [InlineData(502, false)] [InlineData(200, false)]
    public async Task JsonAndHttpResponses_AreNotImplicitlyConverted(int status, bool error)
    {
        var text = Part(error ? "error" : "blocking").GetRawText(); var body = Body(text); using var handler = Reply(body, "application/json", status); using var factory = new DifyHttpFactory(handler);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Collect(new(factory, Options(), Token), TestContext.Current.CancellationToken));
        if (error || status != 200) Assert.Equal(text, Assert.IsType<LlmUpstreamException>(exception).ResponseBody); else Assert.IsType<InvalidDataException>(exception);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
