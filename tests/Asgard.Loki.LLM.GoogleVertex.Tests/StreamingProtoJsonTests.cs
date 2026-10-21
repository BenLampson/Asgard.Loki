using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>原生 ProtoJSON 数值形式不能因为 SDK 过度限制或浮点舍入而被改写/误关联。</summary>
public sealed class StreamingProtoJsonTests
{
    /// <summary>字符串/指数索引与数字枚举保留原始表示，仅用于精确生命周期关联。</summary>
    [Theory] [InlineData("0", "1")] [InlineData("\"0\"", "1")] [InlineData("2e0", "2147483647")] [InlineData("\"7e0\"", "-1")] [InlineData("2147483647", "1e0")]
    public async Task ProtoJsonNumbers_RetainOriginalRepresentation(string index, string finish)
    {
        var data = "{\"candidates\":[{\"index\":" + index + ",\"finishReason\":" + finish + "}]}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        await foreach (var item in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item);
        Assert.Equal(data, Assert.Single(observed).Data.GetRawText()); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>小数、近整数、指数溢出和 int32 越界不能被舍入成有效候选索引。</summary>
    [Theory] [InlineData("0.1")] [InlineData("\"1e-9999\"")] [InlineData("2147483648")] [InlineData("\"1e999999999999999999999\"")] [InlineData("1.00000000000000000000000000000001")]
    public async Task NonExactOrOverflowingIndex_IsRejected(string index)
    {
        var data = "{\"candidates\":[{\"index\":" + index + ",\"finishReason\":\"STOP\"}]}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { } }); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>数字提示拦截是原生失败数据；零枚举不表示提示拦截或候选已结束。</summary>
    [Theory] [InlineData("{\"promptFeedback\":{\"blockReason\":1}}", true)] [InlineData("{\"promptFeedback\":{\"blockReason\":0}}", false)] [InlineData("{\"candidates\":[{\"finishReason\":0}]}", false)]
    public async Task NumericEnum_DistinguishesUnspecifiedWithoutRewriting(string data, bool terminal)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        async Task Consume() { await foreach (var item in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item); }
        if (terminal) { await Consume(); Assert.Equal(data, Assert.Single(observed).Data.GetRawText()); } else await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
