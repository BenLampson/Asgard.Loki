using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>保留原生关联域和未知字段，缺失、重复或晚到生成不能靠 EOF 修补。</summary>
public sealed class ArkStreamStateTests
{
    /// <summary>全局/每 choice 结束、响应关联、已知类型和精确原始 JSON 都须自洽。</summary>
    [Theory] [InlineData("chat-no-done")] [InlineData("chat-duplicate-done")] [InlineData("chat-id")] [InlineData("chat-late-choice")] [InlineData("chat-duplicate-choice")] [InlineData("chat-delta")] [InlineData("chat-choice-limit")] [InlineData("response-no-terminal")] [InlineData("response-early-done")] [InlineData("response-id")] [InlineData("response-status")] [InlineData("response-duplicate")] [InlineData("response-late-reasoning")] [InlineData("duplicate-json")] [InlineData("unicode")] [InlineData("truncated")] [InlineData("utf8")]
    public async Task BrokenNativeLifecycle_DoesNotPublishTerminal(string fault)
    {
        var responses = fault.StartsWith("response-", StringComparison.Ordinal); var frames = Frames(responses); var done = !responses; var policy = new VolcengineHttpOptions();
        switch (fault)
        {
            case "chat-no-done": done = false; break;
            case "chat-id": frames[1] = frames[1].Replace("chat-ark-a", "other", StringComparison.Ordinal); break;
            case "chat-late-choice": frames.Insert(4, frames[1]); break;
            case "chat-duplicate-choice": frames[3] = frames[3].Replace("\"choices\":[", "\"choices\":[{\"index\":0,\"delta\":{}},", StringComparison.Ordinal); break;
            case "chat-delta": frames[1] = "{\"id\":\"chat-ark-a\",\"choices\":[{\"index\":0,\"delta\":false}]}"; break;
            case "chat-choice-limit": frames[1] = frames[1].Replace("\"index\":0", "\"index\":9007199254740993", StringComparison.Ordinal); policy = policy with { MaximumTrackedChoices = 1 }; break;
            case "response-no-terminal": frames.RemoveAt(6); break;
            case "response-early-done": frames = frames.Take(2).ToList(); done = true; break;
            case "response-id": frames[6] = frames[6].Replace("response-ark-a", "other", StringComparison.Ordinal); break;
            case "response-status": frames[6] = frames[6].Replace("\"status\":\"incomplete\"", "\"status\":\"completed\"", StringComparison.Ordinal); break;
            case "response-duplicate": frames.Insert(7, frames[6]); break;
            case "response-late-reasoning": frames.Add(frames[1]); break;
            case "duplicate-json": frames.Add("{\"future\":0,\"future\":1}"); break;
            case "unicode": frames.Add("{\"future\":\"\\ud800\"}"); break;
        }
        var wire = Wire(frames, done); if (fault == "chat-duplicate-done") wire += "data: [DONE]" + Environment.NewLine + Environment.NewLine; if (fault == "truncated") wire = wire.TrimEnd(); var bytes = Bytes(wire); if (fault == "utf8") bytes = [..bytes, 0xff];
        var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); using var factory = new ArkHttpFactory(handler); List<LlmJsonEvent> delivered = [];
        await Assert.ThrowsAnyAsync<Exception>(() => Stream(new(factory, policy), responses, TestContext.Current.CancellationToken, delivered.Add)); Assert.DoesNotContain(delivered, x => Ending(x.Data)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>远端大索引保持原文，以关联数量而非索引值限制内存；不同工具索引不会被重编号。</summary>
    [Fact]
    public async Task SparseChoiceIndex_IsPreservedWithoutArrayAllocation()
    {
        var frames = Frames(false).Select(x => x.Replace("\"index\":0,\"delta\"", "\"index\":9007199254740993,\"delta\"", StringComparison.Ordinal)).ToList(); var body = new OllamaBodyStream(Bytes(Wire(frames, true)));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); using var factory = new ArkHttpFactory(handler); var events = await Stream(new(factory, new VolcengineHttpOptions { MaximumTrackedChoices = 1 }), false, TestContext.Current.CancellationToken);
        Assert.Equal("9007199254740993", events[0].Data.GetProperty("choices")[0].GetProperty("index").GetRawText()); Assert.Equal("call-b", events[0].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("id").GetString()); Assert.Equal(6, events.Count);
    }
    /// <summary>Chat 不以未来根 type 字段的标量类型决定生命周期，未知业务数据按原值保留。</summary>
    [Fact]
    public async Task FutureChatTypeScalar_IsNotAResponsesSchemaField()
    {
        const string future = "{\"type\":false,\"id\":\"business\",\"value\":null}"; var frames = Frames(false); frames.Add(future); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Wire(frames, true))), true))); using var factory = new ArkHttpFactory(handler);
        var events = await Stream(new(factory), false, TestContext.Current.CancellationToken); Assert.Equal(future, events[^1].Data.GetRawText()); Assert.False(events[^1].Data.GetProperty("type").GetBoolean());
    }
    /// <summary>原生最早错误仍完整作为事件交付，错误中的 choices 为空不变成成功或被丢弃。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitialNativeFailure_IsRetainedAfterClosure(bool responses)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"code\":\"native_failure\",\"future\":null},\"choices\":[]}"; var body = new OllamaBodyStream(Bytes(Wire([error], false))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); using var factory = new ArkHttpFactory(handler);
        var events = await Stream(new(factory), responses, TestContext.Current.CancellationToken, _ => { Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.Equal(error, Assert.Single(events).Data.GetRawText());
    }
}
