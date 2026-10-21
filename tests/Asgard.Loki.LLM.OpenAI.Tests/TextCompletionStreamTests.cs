using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.TextCompletionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>文本自己的 choice/text/logprobs 关联、原生错误和 DONE，不能借用 Chat/Responses 的终态语义。</summary>
public sealed class TextCompletionStreamTests
{
    /// <summary>截断、错误索引、选择项重开和重复终态全部拒绝，不因实际 DONE 提前交付成功。</summary>
    [Theory] [InlineData("missing-done")] [InlineData("early-done")] [InlineData("unfinished")] [InlineData("duplicate-done")] [InlineData("after-done")]
    [InlineData("duplicate-index")] [InlineData("negative")] [InlineData("fraction")] [InlineData("overflow")] [InlineData("missing-index")]
    [InlineData("id-drift")] [InlineData("model-drift")] [InlineData("choices-kind")] [InlineData("text-kind")] [InlineData("logs-kind")] [InlineData("tokens-kind")]
    [InlineData("repeated-finish")] [InlineData("late-text")] [InlineData("late-logprobs")] [InlineData("chat-delta")] [InlineData("finish-kind")] [InlineData("finish-empty")]
    public async Task BrokenNativeStream_CannotReleaseEnding(string fault)
    {
        var frames = Events().Select(e => JsonNode.Parse(e.GetRawText())!.AsObject()).ToList(); var first = frames[0]["choices"]![0]!.AsObject(); var finish = frames[2]["choices"]![0]!.AsObject();
        switch (fault)
        {
            case "unfinished": frames.RemoveAt(3); break;
            case "duplicate-index": frames[0]["choices"]!.AsArray().Add(first.DeepClone()); break; case "negative": first["index"] = -1; break; case "fraction": first["index"] = 1.5; break; case "overflow": first["index"] = JsonNode.Parse("9223372036854775808"); break; case "missing-index": first.Remove("index"); break;
            case "id-drift": frames[1]["id"] = "other"; break; case "model-drift": frames[1]["model"] = "other"; break; case "choices-kind": frames[0]["choices"] = true; break; case "text-kind": first["text"] = null; break; case "logs-kind": first["logprobs"] = new JsonArray(); break; case "tokens-kind": first["logprobs"]!["tokens"] = false; break;
            case "repeated-finish": frames.Insert(3, frames[2].DeepClone().AsObject()); break;
            case "late-text": var lateText = frames[2].DeepClone().AsObject(); lateText["choices"]![0]!["text"] = "late"; lateText["choices"]![0]!["finish_reason"] = null; frames.Insert(3, lateText); break;
            case "late-logprobs": var lateLogs = frames[2].DeepClone().AsObject(); lateLogs["choices"]![0]!["logprobs"] = first["logprobs"]!.DeepClone(); lateLogs["choices"]![0]!["finish_reason"] = null; frames.Insert(3, lateLogs); break;
            case "chat-delta": first.Remove("text"); first.Remove("logprobs"); first["delta"] = new JsonObject { ["content"] = "wrong-protocol" }; break;
            case "finish-kind": finish["finish_reason"] = true; break; case "finish-empty": finish["finish_reason"] = ""; break;
        }
        var source = string.Concat(frames.Select(e => Frame(e.ToJsonString()))) + (fault == "missing-done" ? "" : Frame("[DONE]"));
        if (fault == "early-done") source = Frame("[DONE]"); if (fault == "duplicate-done") source += Frame("[DONE]"); if (fault == "after-done") source += Frame("{\"future\":false}");
        var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); List<TextCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => TextCompletionFixture.Stream(new(factory), TestContext.Current.CancellationToken, observe: seen.Add)); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>首个 choice 结束之后及 DONE 之后的原生错误都保留原始错误字节，不交付先前暂存终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateNativeError_DiscardsPendingSuccess(bool afterDone)
    {
        var error = JsonSerializer.Serialize(Part("error")); var source = Wire(Events(), afterDone) + Frame(error); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); List<TextCompletionEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => TextCompletionFixture.Stream(new(factory), TestContext.Current.CancellationToken, observe: seen.Add)); Assert.Equal(Bytes(error), failure.Body.ToArray()); Assert.Equal("text/event-stream", failure.ContentType); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>独立原生错误可不含 DONE，完整原文等到资源关闭后交付；不被认作成功。</summary>
    [Fact]
    public async Task InitialNativeError_IsOriginalAfterClosure()
    {
        var raw = JsonSerializer.Serialize(Part("error")); var body = new OllamaBodyStream(Bytes(Frame(raw))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler);
        var seen = await TextCompletionFixture.Stream(new(factory), TestContext.Current.CancellationToken, observe: _ => { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.True(JsonElement.DeepEquals(Part("error"), Assert.Single(seen).Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>choice 数量按不同原生索引计算，尾帧单独有界，不按巨大索引分配数组。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeStateBudgets_FailWithoutEnding(bool choices)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Text("events.sse"))), true))); var factory = new OpenAiHttpFactory(handler); var policy = choices ? new OpenAiHttpOptions { MaximumTrackedTextChoices = 1 } : new() { MaximumBufferedTerminalEvents = 2 }; List<TextCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => TextCompletionFixture.Stream(new(factory, policy), TestContext.Current.CancellationToken, observe: seen.Add)); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
}
