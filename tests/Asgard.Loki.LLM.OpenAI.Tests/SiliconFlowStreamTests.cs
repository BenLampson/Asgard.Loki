using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.SiliconFlowFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生思考、工具与补全事件必须遵守实际 DONE、身份和资源结束边界。</summary>
public sealed class SiliconFlowStreamTests
{
    /// <summary>每种原生模式缺失实际控制终态时都不能把 finish 或 EOF 当作完成。</summary>
    [Theory] [InlineData("reasoning")] [InlineData("tools")] [InlineData("schema")] [InlineData("json")] [InlineData("media")] [InlineData("prefix")] [InlineData("fim")]
    public async Task MissingDone_DiscardsEnding(string mode)
    {
        var source = Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", ""); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>实际 DONE 后追加未知业务数据或另一结束都明确失败，不泄露成功尾部。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PostDoneData_IsRejected(bool duplicate)
    {
        var source = Part("tools").GetProperty("sse").GetString()!+Frame(duplicate ? "[DONE]" : "{\"type\":\"vendor.trace\",\"native_exact\":9007199254740993}"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "tools"); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, "tools", true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>choice/tool/index 以原文分块保留；同一原生生成的 ID 不能被另一个目标替换。</summary>
    [Fact]
    public async Task NativeIdentityDrift_IsRejected()
    {
        var events = Part("tools").GetProperty("events").EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!).ToArray(); events[2]["id"] = "other-native-response"; var source = string.Concat(events.Select(e => SiliconFlowFixture.Frame(JsonSerializer.SerializeToElement(e))))+Frame("[DONE]"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "tools"); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, "tools", true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>length 原生结果与不完整的 JSON 文本均保持原文，不补括号或伪装 stop。</summary>
    [Fact]
    public async Task LengthOutcome_DoesNotRepairJsonText()
    {
        var events = Part("json").GetProperty("events").EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!).ToArray(); events[1]["choices"]![0]!["delta"]!["content"] = "{\"answer\":\"未完成"; events[2]["choices"]![0]!["finish_reason"] = "length"; var source = string.Concat(events.Select(e => SiliconFlowFixture.Frame(JsonSerializer.SerializeToElement(e))))+Frame("[DONE]"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "json"); List<ChatCompletionEvent> seen = []; await Invoke(sdk, "json", true, seen);
        Assert.Equal(events.Length, seen.Count); for (var i = 0; i < seen.Count; i++) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(events[i]), seen[i].Data)); Assert.Equal("length", Assert.Single(seen, Ending).Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
    /// <summary>结束控制帧之后的实际读取和客户端关闭取消仍撤回原生结束。</summary>
    [Theory] [InlineData("reasoning", false)] [InlineData("reasoning", true)] [InlineData("fim", false)] [InlineData("fim", true)]
    public async Task LateFailure_DiscardsEnding(string mode, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode); List<ChatCompletionEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, true, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前退出不会在后台补全、拼接工具参数、拉取媒体或再次调用目标。</summary>
    [Theory] [InlineData("tools")] [InlineData("media")] [InlineData("fim")]
    public async Task EarlyExit_ReleasesOwnedCall(string mode)
    {
        var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(Request(mode), new() { SessionId = "siliconflow" }, TestContext.Current.CancellationToken)) { Assert.False(Ending(item)); break; }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
