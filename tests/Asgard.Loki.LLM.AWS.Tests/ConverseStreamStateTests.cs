using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>隐式文本开始与显式工具/媒体开始分别验证，缺失尾部不能充当成功。</summary>
public sealed class ConverseStreamStateTests
{
    /// <summary>仅使用有效 CRC 帧构造状态错误，不让二进制格式失败代替生命周期校验。</summary>
    [Theory] [InlineData("missing-start")] [InlineData("duplicate-start")] [InlineData("missing-stop")] [InlineData("missing-metadata")] [InlineData("metadata-before-stop")] [InlineData("duplicate-metadata")] [InlineData("duplicate-stop")] [InlineData("unclosed-block")] [InlineData("unopened-stop")] [InlineData("reopened-index")] [InlineData("duplicate-block")] [InlineData("late-delta")] [InlineData("wrong-delta-kind")] [InlineData("tool-without-start")] [InlineData("result-without-start")] [InlineData("image-without-start")] [InlineData("duplicate-tool-id")] [InlineData("missing-tool-id")] [InlineData("missing-result-id")] [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("quoted-index")] [InlineData("rounded-index")] [InlineData("conflicting-union")] [InlineData("wrong-metadata")]
    public async Task InvalidLifecycle_WithholdsMessageStop(string scenario)
    {
        var text = ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"partial\"}}"); var close = ConverseStreamFixture.Event("contentBlockStop", "{\"contentBlockIndex\":0}"); var tool = ConverseStreamFixture.Event("contentBlockStart", "{\"contentBlockIndex\":0,\"start\":{\"toolUse\":{\"toolUseId\":\"tool-a\",\"name\":\"lookup\"}}}"); var start = ConverseStreamFixture.Start(); var stop = ConverseStreamFixture.Stop(); var meta = ConverseStreamFixture.Metadata();
        byte[][] records = scenario switch
        {
            "missing-start" => [stop, meta], "duplicate-start" => [start, start, stop, meta], "missing-stop" => [start], "missing-metadata" => [start, stop], "metadata-before-stop" => [start, meta, stop], "duplicate-metadata" => [start, stop, meta, meta], "duplicate-stop" => [start, stop, stop, meta], "unclosed-block" => [start, text, stop, meta], "unopened-stop" => [start, close], "reopened-index" => [start, text, close, text], "duplicate-block" => [start, tool, tool], "late-delta" => [start, stop, text, meta],
            "wrong-delta-kind" => [start, tool, text], "tool-without-start" => [start, ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"toolUse\":{\"input\":\"{}\"}}}")], "result-without-start" => [start, ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"toolResult\":[{\"text\":\"x\"}]}}")], "image-without-start" => [start, ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"image\":{\"source\":{\"bytes\":\"AQID\"}}}}")],
            "duplicate-tool-id" => [start, tool, close, ConverseStreamFixture.Event("contentBlockStart", "{\"contentBlockIndex\":1,\"start\":{\"toolUse\":{\"toolUseId\":\"tool-a\",\"name\":\"other\"}}}")], "missing-tool-id" => [start, ConverseStreamFixture.Event("contentBlockStart", "{\"contentBlockIndex\":0,\"start\":{\"toolUse\":{\"name\":\"lookup\"}}}")], "missing-result-id" => [start, ConverseStreamFixture.Event("contentBlockStart", "{\"contentBlockIndex\":0,\"start\":{\"toolResult\":{\"status\":\"error\"}}}")],
            "conflicting-union" => [start, ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"x\",\"toolUse\":{\"input\":\"{}\"}}}")], "wrong-metadata" => [start, stop, ConverseStreamFixture.Event("metadata", "{\"usage\":null,\"metrics\":{}}")],
            _ => [start, ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":" + (scenario == "negative-index" ? "-1" : scenario == "fraction-index" ? "0.1" : scenario == "quoted-index" ? "\"0\"" : "1.00000000000000001") + ",\"delta\":{\"text\":\"x\"}}")]
        };
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(records.SelectMany(item => item).ToArray()))); var factory = new AwsHttpFactory(handler); var finished = false; await Assert.ThrowsAsync<InvalidDataException>(() => ConverseStreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => finished |= item.EventName == "messageStop")); Assert.False(finished); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>文本/推理没有显式 start 也可开始，稀疏数学整数按原始形式保留。</summary>
    [Theory] [InlineData("2147483647", false)] [InlineData("1e2", false)] [InlineData("1.00", true)]
    public async Task ImplicitDeltaStartAndSparseIndices_AreNative(string index, bool reasoning)
    {
        var field = reasoning ? "\"reasoningContent\":{\"signature\":\"opaque\"}" : "\"text\":\"\""; var wire = new[] { ConverseStreamFixture.Start(), ConverseStreamFixture.Event("contentBlockDelta", "{\"contentBlockIndex\":" + index + ",\"delta\":{" + field + "}}"), ConverseStreamFixture.Event("contentBlockStop", "{\"contentBlockIndex\":" + index + "}"), ConverseStreamFixture.Stop("future_stop_reason"), ConverseStreamFixture.Metadata() }.SelectMany(item => item).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var count = 0; await ConverseStreamFixture.Consume(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => { if (item.Data.TryGetProperty("contentBlockIndex", out var raw)) Assert.Equal(index, raw.GetRawText()); if (item.EventName == "messageStop") Assert.Equal("future_stop_reason", item.Data.GetProperty("stopReason").GetString()); count++; }); Assert.Equal(5, count);
    }
}
