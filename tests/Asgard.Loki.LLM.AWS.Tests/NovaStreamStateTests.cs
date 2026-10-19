using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Nova 内层原生信封的工具/内容关联与结束完整性，不改写源字段来修复损坏。</summary>
public sealed class NovaStreamStateTests
{
    /// <summary>原生推理扩展不套用 Converse 的签名/加密字段类型约束，也不会被删除或转成文本。</summary>
    [Fact]
    public async Task OpaqueReasoningExtensions_RemainNative()
    {
        const string delta = "{\"contentBlockDelta\":{\"contentBlockIndex\":0,\"delta\":{\"reasoningContent\":{\"text\":\"[REDACTED]\",\"signature\":{\"opaque\":false},\"redactedContent\":null}}}}";
        var wire = new byte[] { }.Concat(NovaStreamFixture.Start()).Concat(NovaStreamFixture.Chunk(delta)).Concat(NovaStreamFixture.Chunk("{\"contentBlockStop\":{\"contentBlockIndex\":0}}")) .Concat(NovaStreamFixture.Stop()).Concat(NovaStreamFixture.Metadata()).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        await NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true); Assert.Equal(delta, seen[1].Data.GetRawText()); Assert.Equal("contentBlockDelta", seen[1].Type);
    }

    /// <summary>缺失、重复、歧义、错类型和错工具的源均不能释放原生成功终态。</summary>
    [Theory]
    [InlineData("no-start")] [InlineData("repeat-start")] [InlineData("open-block")]
    [InlineData("duplicate-id")] [InlineData("orphan-tool")] [InlineData("wrong-delta")]
    [InlineData("closed-delta")] [InlineData("duplicate-stop")] [InlineData("late-content")]
    [InlineData("missing-stop")] [InlineData("missing-usage")] [InlineData("early-usage")]
    [InlineData("duplicate-usage")] [InlineData("union")] [InlineData("null-event")]
    [InlineData("fraction-index")] [InlineData("string-index")]
    public async Task InvalidSource_HasNoSuccessfulTerminal(string fault)
    {
        var data = NovaStreamFixture.Events().EnumerateArray().Take(16).Select(item => JsonNode.Parse(item.GetProperty("data").GetRawText())!.AsObject()).ToList();
        switch (fault)
        {
            case "no-start": data.RemoveAt(0); break;
            case "repeat-start": data.Insert(1, data[0].DeepClone().AsObject()); break;
            case "open-block": data.RemoveAt(11); break;
            case "duplicate-id": data[7]["contentBlockStart"]!["start"]!["toolUse"]!["toolUseId"] = "call_a"; break;
            case "orphan-tool": data.RemoveAt(5); break;
            case "wrong-delta": data[8]["contentBlockDelta"]!["delta"] = new JsonObject { ["text"] = "wrong" }; break;
            case "closed-delta": data.Insert(12, data[8].DeepClone().AsObject()); break;
            case "duplicate-stop": data.Insert(14, data[13].DeepClone().AsObject()); break;
            case "late-content": data.Add(data[3].DeepClone().AsObject()); break;
            case "missing-stop": data.RemoveAt(13); break;
            case "missing-usage": data.RemoveAt(14); break;
            case "early-usage": var metadata = data[14]; data.RemoveAt(14); data.Insert(0, metadata); break;
            case "duplicate-usage": data.Add(data[14].DeepClone().AsObject()); break;
            case "union": data[0]["messageStop"] = new JsonObject { ["stopReason"] = "end_turn" }; break;
            case "null-event": data[0]["messageStart"] = null; break;
            case "fraction-index": data[1]["contentBlockDelta"]!["contentBlockIndex"] = JsonNode.Parse("0.00000000000000000001"); break;
            case "string-index": data[1]["contentBlockDelta"]!["contentBlockIndex"] = "0"; break;
        }
        var wire = data.SelectMany(item => NovaStreamFixture.Chunk(item.ToJsonString())).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已知用量信封的未知字段保持原样，可选 metrics 存在时必须是对象。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeMetadata_WithOptionalMetrics_IsPreserved(bool metrics)
    {
        var envelope = metrics ? "{\"metadata\":{\"usage\":{\"inputTokens\":2,\"outputTokens\":1,\"totalTokens\":3,\"future\":false},\"metrics\":{\"latencyMs\":0,\"unknown\":null}},\"type\":\"error\"}" : "{\"metadata\":{\"usage\":{\"inputTokens\":2,\"outputTokens\":1,\"totalTokens\":3,\"future\":false}},\"type\":\"error\"}";
        var wire = new byte[] { }.Concat(NovaStreamFixture.Start()).Concat(NovaStreamFixture.Stop()).Concat(NovaStreamFixture.Chunk(envelope)).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        await NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true); Assert.Equal(envelope, seen[^1].Data.GetRawText()); Assert.Equal("metadata", seen[^1].Type); Assert.Equal("chunk", seen[^1].EventName);
    }
    /// <summary>未知媒体/结果分支保持被动原生数据，不借用其他协议的字段格式或假设。</summary>
    [Fact]
    public async Task UnknownNativeBranch_IsPreservedWithAssociationOnly()
    {
        const string delta = "{\"contentBlockDelta\":{\"contentBlockIndex\":2147483647,\"delta\":{\"toolResult\":{\"future\":false},\"image\":null,\"type\":\"error\"}}}";
        var wire = new byte[] { }.Concat(NovaStreamFixture.Start()).Concat(NovaStreamFixture.Chunk(delta)).Concat(NovaStreamFixture.Chunk("{\"contentBlockStop\":{\"contentBlockIndex\":2147483647}}")) .Concat(NovaStreamFixture.Stop()).Concat(NovaStreamFixture.Metadata()).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); List<BedrockNovaEvent> seen = [];
        await NovaStreamFixture.Drain(new(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), TestContext.Current.CancellationToken, seen, apiKey: true); Assert.Equal(delta, seen[1].Data.GetRawText()); Assert.Equal("contentBlockDelta", seen[1].Type); Assert.Equal(5, seen.Count);
    }
}
