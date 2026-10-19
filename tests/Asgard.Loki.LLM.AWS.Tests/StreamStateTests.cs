using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>原生消息/内容块/工具关联与结束条件，CRC 正确也不能掩盖协议截断。</summary>
public sealed class StreamStateTests
{
    /// <summary>缺失/重复/重开、错误工具关联或不完整终态都不返回成功尾部。</summary>
    [Theory] [InlineData("missing-start")] [InlineData("duplicate-start")] [InlineData("missing-stop")] [InlineData("missing-reason")] [InlineData("unclosed-block")] [InlineData("duplicate-stop")] [InlineData("changed-reason")] [InlineData("reopened-block")] [InlineData("unopened-delta")] [InlineData("closed-delta")] [InlineData("wrong-delta")] [InlineData("duplicate-tool-id")] [InlineData("missing-tool-id")] [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("quoted-index")] [InlineData("rounded-index")]
    public async Task InvalidLifecycle_DoesNotReleaseSuccessfulTerminal(string scenario)
    {
        const string block = "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"; const string close = "{\"type\":\"content_block_stop\",\"index\":0}"; const string text = "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}"; const string tool = "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"duplicate\",\"name\":\"lookup\",\"input\":{}}}";
        string[] records = scenario switch
        {
            "missing-start" => [StreamFixture.Delta, StreamFixture.Stop], "duplicate-start" => [StreamFixture.Start, StreamFixture.Start], "missing-stop" => [StreamFixture.Start, StreamFixture.Delta], "missing-reason" => [StreamFixture.Start, StreamFixture.Stop], "unclosed-block" => [StreamFixture.Start, block, StreamFixture.Delta, StreamFixture.Stop], "duplicate-stop" => [StreamFixture.Start, StreamFixture.Delta, StreamFixture.Stop, StreamFixture.Stop], "changed-reason" => [StreamFixture.Start, StreamFixture.Delta, "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}", StreamFixture.Stop], "reopened-block" => [StreamFixture.Start, block, close, block], "unopened-delta" => [StreamFixture.Start, text], "closed-delta" => [StreamFixture.Start, block, close, text],
            "wrong-delta" => [StreamFixture.Start, block, "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}"], "duplicate-tool-id" => [StreamFixture.Start, tool, close, tool.Replace("\"index\":0", "\"index\":1")], "missing-tool-id" => [StreamFixture.Start, tool.Replace("\"id\":\"duplicate\",", "")],
            _ => [StreamFixture.Start, block.Replace("\"index\":0", "\"index\":" + (scenario == "negative-index" ? "-1" : scenario == "fraction-index" ? "0.1" : scenario == "quoted-index" ? "\"0\"" : "1.00000000000000001"))]
        };
        var wire = records.SelectMany(StreamFixture.Chunk).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var factory = new AwsHttpFactory(handler); var finished = false; await Assert.ThrowsAsync<InvalidDataException>(() => StreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => { finished |= item.Type is "message_stop" or "message_delta"; })); Assert.False(finished); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>精确数学整数可使用指数或小数形式，原始 JSON 值不被重编号/舍入。</summary>
    [Theory] [InlineData("2147483647")] [InlineData("1e2")] [InlineData("1.00")]
    public async Task SparseNumericIndices_RetainOriginalRepresentation(string index)
    {
        var records = new[] { StreamFixture.Start, "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"future_kind\",\"nil\":null}}", "{\"type\":\"content_block_stop\",\"index\":" + index + "}", StreamFixture.Delta, StreamFixture.Stop }; var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(records.SelectMany(StreamFixture.Chunk).ToArray()))); var count = 0; await StreamFixture.Consume(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => { if (item.Data.TryGetProperty("index", out var raw)) Assert.Equal(index, raw.GetRawText()); count++; }); Assert.Equal(5, count);
    }
}
