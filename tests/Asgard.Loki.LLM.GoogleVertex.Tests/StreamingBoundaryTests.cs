using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>关联与终态错误不应通过丢帧、补 index、改结束原因或 EOF 合成成功来掩盖。</summary>
public sealed class StreamingBoundaryTests
{
    /// <summary>截断、重复、错误候选/身份和结束后的内容均不能释放暂存终态。</summary>
    [Theory] [InlineData("missing-finish")] [InlineData("unspecified")] [InlineData("duplicate-finish")] [InlineData("late-content")] [InlineData("partial-candidate")] [InlineData("response-id")] [InlineData("model-version")] [InlineData("duplicate-index")] [InlineData("index-type")] [InlineData("negative-index")] [InlineData("finish-type")] [InlineData("truncated-frame")] [InlineData("late-block")] [InlineData("after-block")] [InlineData("duplicate-block")]
    public async Task InvalidLifecycle_WithholdsNativeTerminal(string failure)
    {
        var text = VertexFixture.Sse().Replace("\r\n", "\n"); var finish = "data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n"; var block = "data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}\n\n";
        text = failure switch
        {
            "missing-finish" => "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n",
            "unspecified" => "data: {\"candidates\":[{\"finishReason\":\"FINISH_REASON_UNSPECIFIED\"}]}\n\n",
            "duplicate-finish" => text + finish,
            "late-content" => text + "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"late\"}]}}]}\n\n",
            "partial-candidate" => "data: {\"candidates\":[{\"index\":2,\"finishReason\":\"STOP\"},{\"index\":7,\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n",
            "response-id" => text.Replace("\"responseId\":\"vertex-stream\",\"candidates\":[]", "\"responseId\":\"different\",\"candidates\":[]"),
            "model-version" => text + "data: {\"modelVersion\":\"changed\"}\n\n",
            "duplicate-index" => "data: {\"candidates\":[{\"index\":0},{}]}\n\n", "index-type" => "data: {\"candidates\":[{\"index\":true}]}\n\n", "negative-index" => "data: {\"candidates\":[{\"index\":-1}]}\n\n",
            "finish-type" => "data: {\"candidates\":[{\"finishReason\":true}]}\n\n", "truncated-frame" => text.TrimEnd('\n'),
            "late-block" => text + block, "after-block" => block + finish, _ => block + block
        };
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item); });
        Assert.DoesNotContain(observed, item => item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is not ("" or "FINISH_REASON_UNSPECIFIED")));
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始候选索引可以稀疏且交错，第一次 finish 后所有事件保持原序。</summary>
    [Theory] [InlineData(2)] [InlineData(1)]
    public async Task MultipleCandidates_PreserveSparseAssociationAndLimits(int maximum)
    {
        const string text = "data: {\"candidates\":[{\"index\":2,\"finishReason\":\"STOP\"},{\"index\":7,\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\ndata: {\"candidates\":[{\"index\":7,\"finishReason\":\"MAX_TOKENS\"}]}\n\n";
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumTrackedCandidates = maximum }, timeProvider: clock); var observed = new List<GeminiContentEvent>();
        async Task Consume() { await foreach (var item in StreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) { Assert.True(stream.Disposed); observed.Add(item); } }
        if (maximum == 1) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Empty(observed); } else { await Consume(); Assert.Equal(2, observed.Count); Assert.Equal(2, observed[0].Data.GetProperty("candidates")[0].GetProperty("index").GetInt32()); Assert.Equal(7, observed[0].Data.GetProperty("candidates")[1].GetProperty("index").GetInt32()); Assert.Equal(7, observed[1].Data.GetProperty("candidates")[0].GetProperty("index").GetInt32()); }
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>终态、未知元数据和用量都计入尾部上限，不能截掉尾帧获得成功。</summary>
    [Theory] [InlineData(3)] [InlineData(2)]
    public async Task TerminalTailLimit_IsExact(int maximum)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBufferedTerminalEvents = maximum }, timeProvider: clock); var count = 0;
        async Task Consume() { await foreach (var _ in StreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) count++; }
        if (maximum == 2) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(2, count); } else { await Consume(); Assert.Equal(5, count); } Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原始 SSE 字节上限包括注释与所有未知字段，不按字符或解码后内容计算。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TotalByteLimit_IsExact(bool tooSmall)
    {
        var bytes = Encoding.UTF8.GetBytes(VertexFixture.Sse()); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBodyBytes = bytes.Length - (tooSmall ? 1 : 0) }, timeProvider: clock); var count = 0;
        async Task Consume() { await foreach (var _ in StreamingTests.Enumerate(provider, TestContext.Current.CancellationToken)) count++; }
        if (tooSmall) { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.Equal(2, count); } else { await Consume(); Assert.Equal(5, count); } Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>Vertex 不以 Chat DONE 或 JSON/UTF-8 修复来替代原生候选终态。</summary>
    [Theory] [InlineData("done")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("encoding")]
    public async Task InvalidSseData_IsNotRepaired(string failure)
    {
        var bytes = failure == "encoding" ? Encoding.UTF8.GetBytes("data: {\"future\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}\n\n")).ToArray() : Encoding.UTF8.GetBytes(failure switch { "done" => VertexFixture.Sse() + "data: [DONE]\n\n", "duplicate" => "data: {\"future\":1,\"future\":2}\n\n", _ => "data: {\"future\":\"\\ud800\"}\n\n" });
        var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock();
        async Task Consume() { await foreach (var _ in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { } }
        if (failure == "encoding") await Assert.ThrowsAsync<DecoderFallbackException>(Consume); else if (failure == "escape") await Assert.ThrowsAsync<InvalidDataException>(Consume); else await Assert.ThrowsAnyAsync<JsonException>(Consume); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
