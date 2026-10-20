using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.MiniMax.Tests.MiniMaxFixture;

namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>原生结束分块与重复结束原因的完整快照不是同一种状态，不合成任何终态。</summary>
public sealed class MiniMaxStateTests
{
    /// <summary>无完整快照、带实际 DONE、单独完整快照及未知业务尾帧均保留各自原生含义。</summary>
    [Theory] [InlineData("finish-only")] [InlineData("done")] [InlineData("snapshot-only")] [InlineData("metadata")]
    public async Task NativeValidEndings_ArePreserved(string variant)
    {
        var events = Golden.GetProperty("events").EnumerateArray().ToList(); var source = Golden.GetProperty("sse").GetString()!;
        if (variant == "finish-only") { events.RemoveAt(2); source = string.Concat(events.Select(Frame)); }
        if (variant == "done") source += "data: [DONE]\n\n";
        if (variant == "snapshot-only") { events = [events[2]]; source = Frame(events[0]); }
        if (variant == "metadata") { events.Add(Golden.GetProperty("business_metadata")); source += Frame(events[^1]); }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new MiniMaxHttpFactory(handler); List<ChatCompletionEvent> seen = []; await Drain(new(factory), TestContext.Current.CancellationToken, seen);
        Assert.Equal(events.Count, seen.Count); for (var i = 0; i < events.Count; i++) Assert.True(JsonElement.DeepEquals(events[i], seen[i].Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>大整数稀疏 choice 按实际标识关联，允许另一个未结束 choice 继续生成。</summary>
    [Fact]
    public async Task SparseInterleavedChoices_PreserveIndicesAndNativeOutcomes()
    {
        const long sparse = 9007199254740993;
        var records = new[] { new { choices = new object[] { new { index = sparse, delta = new { content = "A" } }, new { index = 0L, delta = new { content = "B" } } } }, new { choices = new object[] { new { index = sparse, delta = new { }, finish_reason = "length" } } }, new { choices = new object[] { new { index = 0L, delta = new { content = "C" }, finish_reason = "future_native_reason" } } } };
        var source = string.Concat(records.Select(record => "data: "+JsonSerializer.Serialize(record)+"\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new MiniMaxHttpFactory(handler); List<ChatCompletionEvent> seen = []; await Drain(new(factory, new() { MaximumTrackedChoices = 2 }), TestContext.Current.CancellationToken, seen); Assert.Equal(3, seen.Count); Assert.Equal(sparse, seen[1].Data.GetProperty("choices")[0].GetProperty("index").GetInt64()); Assert.Equal("future_native_reason", seen[2].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
    /// <summary>错误、截断、身份错配及资源边界不得释放暂存成功尾部。</summary>
    [Theory] [InlineData("missing-finish")] [InlineData("late-delta")] [InlineData("duplicate-finish")] [InlineData("reason-mismatch")] [InlineData("changed-id")]
    [InlineData("duplicate-snapshot")] [InlineData("string-index")] [InlineData("fraction-index")] [InlineData("negative-index")] [InlineData("open-choice")]
    [InlineData("early-done")] [InlineData("post-done")] [InlineData("repeated-done")] [InlineData("choice-limit")] [InlineData("tail-limit")] [InlineData("snapshot-message")]
    public async Task InvalidNativeLifecycle_IsRejected(string fault)
    {
        var events = Golden.GetProperty("events").EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!).ToList(); var options = new MiniMaxLlmOptions();
        switch (fault)
        {
            case "missing-finish": events = [events[0]]; break;
            case "late-delta": events = [events[0], events[1], events[0].DeepClone()]; break;
            case "duplicate-finish": events = [events[0], events[1], events[1].DeepClone()]; break;
            case "reason-mismatch": events[2]["choices"]![0]!["finish_reason"] = "length"; break;
            case "changed-id": events[1]["id"] = "another-native-id"; break;
            case "duplicate-snapshot": events.Add(events[2].DeepClone()); break;
            case "string-index": events[0]["choices"]![0]!["index"] = "0"; break;
            case "fraction-index": events[0]["choices"]![0]!["index"] = 0.5; break;
            case "negative-index": events[0]["choices"]![0]!["index"] = -1; break;
            case "open-choice": events = [events[0], events[1]]; events[1]["choices"]!.AsArray().Add(JsonNode.Parse("{\"index\":1,\"delta\":{\"content\":\"unfinished\"}}")); break;
            case "choice-limit": events[0]["choices"]!.AsArray().Add(JsonNode.Parse("{\"index\":1,\"delta\":{}}")); options = options with { MaximumTrackedChoices = 1 }; break;
            case "tail-limit": options = options with { MaximumBufferedTerminalEvents = 1 }; break;
            case "snapshot-message": events[2]["choices"]![0]!.AsObject().Remove("message"); break;
        }
        var source = string.Concat(events.Select(e => "data: "+e.ToJsonString()+"\n\n")); if (fault == "early-done") source = "data: [DONE]\n\n"+source; if (fault == "post-done") source += "data: [DONE]\n\ndata: {}\n\n"; if (fault == "repeated-done") source += "data: [DONE]\n\ndata: [DONE]\n\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new MiniMaxHttpFactory(handler); List<ChatCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(new(factory, options), TestContext.Current.CancellationToken, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
