using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Mantle SSE 使用原生内容/工具关联状态，不通过数组重编号掩盖错误。</summary>
public sealed class MantleStateTests
{
    /// <summary>工具/内容错误、未闭合和重复或缺失终态都必须拒绝成功。</summary>
    [Theory]
    [InlineData("orphan")] [InlineData("duplicate-index")] [InlineData("duplicate-tool")] [InlineData("wrong-delta")]
    [InlineData("open")] [InlineData("reopen")] [InlineData("duplicate-stop")] [InlineData("truncated")]
    public async Task InvalidLifecycle_HasNoSuccessfulTerminal(string kind)
    {
        var events = Golden("stream").GetProperty("events").EnumerateArray().Select(item => JsonNode.Parse(item.GetProperty("data").GetRawText())!.AsObject()).ToList();
        switch (kind)
        {
            case "orphan": events[2]["index"] = 99; break;
            case "duplicate-index": events.Insert(5, events[1].DeepClone().AsObject()); break;
            case "duplicate-tool": var repeated = events[10].DeepClone().AsObject(); repeated["index"] = 4; events.Insert(14, repeated); break;
            case "wrong-delta": events[8]["delta"]!["type"] = "input_json_delta"; break;
            case "open": events.RemoveAt(13); break;
            case "reopen": events.Add(events[7].DeepClone().AsObject()); break;
            case "duplicate-stop": events.Insert(16, events[15].DeepClone().AsObject()); break;
            case "truncated": events.RemoveAt(15); break;
        }
        var clock = new AwsClock(); var wire = string.Concat(events.Select(item => Frame(item.ToJsonString()))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); List<ClaudeMessageEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>本地块与尾部限额共享原生状态，但不会修剪上游字段或帧。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StateLimits_FailWithoutTrimming(bool tail)
    {
        var clock = new AwsClock(); var wire = Golden("stream").GetProperty("wire").GetString()!; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); List<ClaudeMessageEvent> seen = [];
        var provider = new AwsMantleLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumTrackedContentBlocks = tail ? 128 : 1, MaximumBufferedTerminalEvents = tail ? 1 : 128 }, timeProvider: clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(provider, TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop");
    }
}
