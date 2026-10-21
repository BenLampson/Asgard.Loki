using System.Text;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cohere.Tests.CohereFixture;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>原生开始/结束、身份、晚到生成和资源尾部故障不能先交付终态。</summary>
public sealed class CohereTerminalTests
{
    /// <summary>已观察到 stream-end 也需完整有效的原生窗口，不能隐藏迟到生成、身份或结束原因错误。</summary>
    [Theory]
    [InlineData("missing-start")] [InlineData("missing-end")] [InlineData("duplicate-start")] [InlineData("duplicate-end")]
    [InlineData("early-text")] [InlineData("early-tool")] [InlineData("late-text")] [InlineData("late-tool")]
    [InlineData("changed-id")] [InlineData("end-id")] [InlineData("reason-mismatch")] [InlineData("flag-early")]
    [InlineData("flag-end")] [InlineData("flag-type")] [InlineData("no-response")] [InlineData("no-text")]
    [InlineData("no-reason")] [InlineData("null-reason")] [InlineData("no-event-type")] [InlineData("empty-start-id")]
    public async Task InvalidNativeWindow_DoesNotPublishTerminal(string kind)
    {
        var end = End; var records = new List<string> { Start, Partial, end };
        switch (kind)
        {
            case "missing-start": records.RemoveAt(0); break;
            case "missing-end": records.RemoveAt(2); break;
            case "duplicate-start": records.Insert(1, Start); break;
            case "duplicate-end": records.Add(End); break;
            case "early-text": records.Insert(0, Partial); break;
            case "early-tool": records.Insert(0, "{\"event_type\":\"tool-calls-chunk\",\"tool_call_delta\":{\"index\":0}}"); break;
            case "late-text": records.Add(Partial); break;
            case "late-tool": records.Add("{\"event_type\":\"tool-calls-generation\",\"tool_calls\":[]}"); break;
            case "changed-id": records.Insert(2, "{\"event_type\":\"debug\",\"generation_id\":\"changed\"}"); break;
            case "end-id": records[2] = End.Replace("\"generation_id\":\"g\"", "\"generation_id\":\"other\""); break;
            case "reason-mismatch": records[2] = End.Replace("\"response\":{", "\"response\":{\"finish_reason\":\"ERROR\","); break;
            case "flag-early": records[1] = Partial[..^1] + ",\"is_finished\":true}"; break;
            case "flag-end": records[2] = End[..^1] + ",\"is_finished\":false}"; break;
            case "flag-type": records[0] = Start[..^1] + ",\"is_finished\":0}"; break;
            case "no-response": records[2] = "{\"event_type\":\"stream-end\",\"finish_reason\":\"COMPLETE\"}"; break;
            case "no-text": records[2] = "{\"event_type\":\"stream-end\",\"finish_reason\":\"COMPLETE\",\"response\":{}}"; break;
            case "no-reason": records[2] = End.Replace("\"finish_reason\":\"COMPLETE\",", ""); break;
            case "null-reason": records[2] = End.Replace("\"COMPLETE\"", "null"); break;
            case "no-event-type": records[1] = "{\"type\":\"stream-end\",\"text\":\"business\"}"; break;
            case "empty-start-id": records[0] = Start.Replace("\"g\"", "\"\""); break;
        }
        var body = new CohereBodyStream(Encoding.UTF8.GetBytes(string.Join("\n", records))); using var handler = CohereNativeTests.Response(body, "application/x-ndjson");
        using var factory = new CohereHttpFactory(handler); var provider = new CohereLlmProvider(factory); List<CohereChatEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, e => e.IsTerminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>EOF、流或客户端释放发生故障/取消时，已保留的终态及尾部都不得先交付。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")]
    [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task LateTransportFailure_HoldsTerminalUntilClosure(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + Partial + "\n" + End + "\n{\"event_type\":\"future-metadata\",\"value\":null}\n"))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = CohereNativeTests.Response(body, "application/ndjson"); using var factory = new CohereHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("client dispose") : kind == "cancel-client" ? cancel.Cancel : null };
        var provider = new CohereLlmProvider(factory); List<CohereChatEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), cancel.Token)) seen.Add(item); });
        Assert.NotNull(failure); if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.Equal(2, seen.Count); Assert.DoesNotContain(seen, e => e.IsTerminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>流式错误保留原始记录及 HTTP 状态，终态之后的错误也不能被吞掉。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitRootError_IsNotHiddenAfterEnd(bool afterEnd)
    {
        const string error = "{\"error\":{\"message\":\"native error\",\"future\":null}}";
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + (afterEnd ? End + "\n" : "") + error + "\n")), "application/json");
        var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); var terminals = 0;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) if (item.IsTerminal) terminals++; });
        Assert.Equal(System.Net.HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(0, terminals); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生错误/长度结束与未来原因作为终态原样交付，不映射 stop 或伪装成功。</summary>
    [Theory] [InlineData("ERROR")] [InlineData("ERROR_LIMIT")] [InlineData("ERROR_TOXIC")] [InlineData("MAX_TOKENS")] [InlineData("FUTURE_REASON")]
    public async Task NativeFinishReason_RemainsVisible(string reason)
    {
        var end = End.Replace("COMPLETE", reason); using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + end)), "application/x-ndjson");
        var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); List<CohereChatEvent> events = [];
        await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(2, events.Count); Assert.Equal(end, events[^1].Data.GetRawText()); Assert.Equal(reason, events[^1].FinishReason); Assert.True(events[^1].IsTerminal);
    }
    /// <summary>允许空白、CRLF、未知元数据和无尾换行，不将业务 type/error 字段视作生命周期。</summary>
    [Theory] [InlineData("\n", false)] [InlineData("\n", true)] [InlineData("\r\n", false)] [InlineData("\r\n", true)]
    public async Task NativeFramingAndUnknownTail_RemainOrdered(string newline, bool trailing)
    {
        const string tail = "{\"event_type\":\"future-metadata\",\"type\":\"text-generation\",\"future\":{\"error\":true,\"is_finished\":true}}";
        var wire = " \t" + newline + Start + newline + newline + End + newline + tail + (trailing ? newline : "");
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(wire)), "application/json"); var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); List<CohereChatEvent> events = [];
        await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(3, events.Count); Assert.True(events[1].IsTerminal); Assert.Equal(tail, events[2].Data.GetRawText()); Assert.False(events[2].IsTerminal);
    }
    /// <summary>消费方提前结束只关闭本次连接，不额外读取后续终态或重发调用。</summary>
    [Fact]
    public async Task EarlyConsumerExit_ClosesOnlyOwnedResources()
    {
        var body = new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + End + "\n")); using var handler = CohereNativeTests.Response(body, "application/json"); using var factory = new CohereHttpFactory(handler); var provider = new CohereLlmProvider(factory);
        await using (var events = provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken)) Assert.True(await events.MoveNextAsync());
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>保留尾部有显式记录预算，超出时不能先返回已读 stream-end。</summary>
    [Fact]
    public async Task TailRecordBudget_PreventsUnboundedTerminalBuffer()
    {
        var wire = Start + "\n" + End + "\n{\"event_type\":\"future\"}\n{\"event_type\":\"future\"}\n";
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(wire)), "application/json"); var provider = new CohereLlmProvider(new CohereHttpFactory(handler), options: new() { MaximumBufferedTailRecords = 2 });
        var terminal = false; await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; }); Assert.False(terminal);
    }
}
