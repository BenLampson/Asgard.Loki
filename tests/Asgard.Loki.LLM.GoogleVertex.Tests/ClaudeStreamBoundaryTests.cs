using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Claude 消息/内容块生命周期的缺失、重开、错关联与冲突必须显式失败。</summary>
public sealed class ClaudeStreamBoundaryTests
{
    /// <summary>无完整消息终态、未闭合/错关联内容或重复终态不能被 EOF 伪装成成功。</summary>
    [Theory] [InlineData("missing-stop")] [InlineData("missing-reason")] [InlineData("duplicate-start")] [InlineData("duplicate-stop")] [InlineData("unknown-index")] [InlineData("closed-index")] [InlineData("reopened-index")] [InlineData("duplicate-tool")] [InlineData("delta-type")] [InlineData("unclosed-block")] [InlineData("changed-reason")] [InlineData("after-stop")] [InlineData("before-start")] [InlineData("mismatched-event")] [InlineData("truncated-frame")]
    public async Task InvalidLifecycle_DoesNotReleaseMessageFinish(string failure)
    {
        var text = ClaudeFixture.Sse().Replace("\r\n", "\n"); var start = ClaudeStreamingTests.Start;
        const string block = "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n";
        const string delta = "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}\n\n";
        const string close = "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n";
        const string finish = "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n";
        text = failure switch
        {
            "missing-stop" => text[..text.IndexOf("event: message_stop", StringComparison.Ordinal)], "missing-reason" => start + "data: {\"type\":\"message_stop\"}\n\n", "duplicate-start" => start + start,
            "duplicate-stop" => text + "data: {\"type\":\"message_stop\"}\n\n", "unknown-index" => start + delta, "closed-index" => start + block + close + delta, "reopened-index" => start + block + close + block,
            "duplicate-tool" => start + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"same\",\"name\":\"lookup\",\"input\":{}}}\n\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"same\",\"name\":\"other\",\"input\":{}}}\n\n",
            "delta-type" => start + block + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}\n\n", "unclosed-block" => start + block + finish,
            "changed-reason" => start + finish + finish.Replace("end_turn", "max_tokens"), "after-stop" => text + block, "before-start" => block, "mismatched-event" => "event: message_stop\ndata: {\"type\":\"ping\"}\n\n", _ => text.TrimEnd('\n')
        };
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<ClaudeMessageEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in ClaudeStreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item); });
        Assert.DoesNotContain(observed, item => item.Type is "message_delta" or "message_stop"); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>内容块按稀疏索引关联，未知块/增量保留，不按索引展开数组。</summary>
    [Theory] [InlineData(2)] [InlineData(1)]
    public async Task SparseAndFutureBlocks_RetainRawAssociation(int maximum)
    {
        const string blocks = "data: {\"type\":\"content_block_start\",\"index\":7,\"content_block\":{\"type\":\"future_block\",\"future\":false}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":7,\"delta\":{\"type\":\"future_delta\",\"integer\":9007199254740993}}\n\ndata: {\"type\":\"content_block_stop\",\"index\":7}\n\ndata: {\"type\":\"content_block_start\",\"index\":2147483647,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"opaque-redacted\"}}\n\ndata: {\"type\":\"content_block_stop\",\"index\":2147483647}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n";
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeStreamingTests.Start + blocks)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumTrackedClaudeBlocks = maximum }, timeProvider: clock); var observed = new List<ClaudeMessageEvent>();
        async Task Consume() { await foreach (var item in ClaudeStreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) observed.Add(item); }
        if (maximum == 1) await Assert.ThrowsAsync<InvalidDataException>(Consume); else { await Consume(); Assert.Equal(7, observed[1].Data.GetProperty("index").GetInt32()); Assert.Equal(9007199254740993L, observed[2].Data.GetProperty("delta").GetProperty("integer").GetInt64()); Assert.Equal(int.MaxValue, observed[4].Data.GetProperty("index").GetInt32()); }
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>待交付尾部包含原生 stop reason 与 message_stop，超限不能丢掉其中之一。</summary>
    [Theory] [InlineData(2)] [InlineData(1)]
    public async Task TerminalTailLimit_IsExact(int maximum)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBufferedTerminalEvents = maximum }, timeProvider: clock); var count = 0;
        async Task Consume() { await foreach (var _ in ClaudeStreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) count++; }
        if (maximum == 1) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(11, count); } else { await Consume(); Assert.Equal(13, count); } Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原始字节限制包括所有 SSE 元数据和注释，精确边界可用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RawByteLimit_IsExact(bool tooSmall)
    {
        var bytes = Encoding.UTF8.GetBytes(ClaudeFixture.Sse()); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBodyBytes = bytes.Length - (tooSmall ? 1 : 0) }, timeProvider: clock); var count = 0;
        async Task Consume() { await foreach (var _ in ClaudeStreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) count++; }
        if (tooSmall) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(11, count); } else { await Consume(); Assert.Equal(13, count); } Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
