using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.DelegatedNativeFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>兼容原生协议的真实终态、原生失败、调用者配置与资源结束边界。</summary>
public sealed class DelegatedNativeBoundaryTests
{
    /// <summary>分别观察已声明的七个原生配置，不从适配器类上的方法推断端点能力。</summary>
    public static IEnumerable<object[]> Cases() => Modes.Select(mode => new object[] { mode });
    /// <summary>缺少真实控制帧或响应终态，不能把 finish、用量或 EOF 当作完成。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task MissingTerminal_DiscardsEnding(string mode)
    {
        var source = Protocol(mode) == "responses" ? string.Concat(Part(mode).GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "response.completed").Select(DelegatedNativeFixture.Frame)) : Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", "");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>控制结束后不能再追加已知生成内容或重复实际结束。</summary>
    [Theory] [InlineData("openrouter-chat", false)] [InlineData("openrouter-chat", true)] [InlineData("xinference-text", false)] [InlineData("xinference-text", true)] [InlineData("openrouter-responses", false)] [InlineData("openrouter-responses", true)]
    public async Task PostTerminal_IsRejected(string mode, bool duplicate)
    {
        var after = Protocol(mode) == "responses" ? duplicate ? JsonSerializer.Serialize(Part(mode).GetProperty("events").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "response.completed")) : "{\"type\":\"response.output_text.delta\",\"delta\":\"late\"}" : duplicate ? "[DONE]" : "{\"choices\":[{\"index\":0,\"text\":\"late\"}]}";
        var source = Part(mode).GetProperty("sse").GetString()!+OpenAiHttpFixture.Frame(after); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>首帧或普通内容之后的原生流错误无需虚构 DONE；原始 error/finish_reason=error 保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeStreamingError_IsOriginal(bool afterContent)
    {
        const string mode = "openrouter-chat"; var error = Window.GetProperty("stream_error"); var source = (afterContent ? DelegatedNativeFixture.Frame(Part(mode).GetProperty("events")[0]) : "")+DelegatedNativeFixture.Frame(error); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); Assert.Equal(afterContent ? 2 : 1, seen.Count); Assert.True(JsonElement.DeepEquals(error, seen[^1].Data)); Assert.Equal("error", seen[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已经出现普通 finish 的错误撤回全部缓冲终态，异常保留原始错误正文。</summary>
    [Fact]
    public async Task ErrorAfterFinish_DiscardsEnding()
    {
        const string mode = "openrouter-chat"; var error = JsonSerializer.Serialize(Window.GetProperty("stream_error")); var source = Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", OpenAiHttpFixture.Frame(error)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, true, seen)); Assert.Equal(error, failure.ResponseBody); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实 EOF 读取失败或客户端释放时取消，仍不得交付成功结束。</summary>
    [Theory] [InlineData("openrouter-chat", false)] [InlineData("openrouter-chat", true)] [InlineData("xinference-text", false)] [InlineData("xinference-text", true)] [InlineData("openrouter-responses", false)] [InlineData("openrouter-responses", true)]
    public async Task LateFailure_DiscardsEnding(string mode, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, true, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>终止枚举只释放当前自有资源，不在后台补流或重试。</summary>
    [Theory] [InlineData("openrouter-chat")] [InlineData("xinference-text")] [InlineData("openrouter-responses")]
    public async Task EarlyExit_ReleasesCall(string mode)
    {
        var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); var token = TestContext.Current.CancellationToken; var call = new LlmCallOptions { SessionId = "delegated" };
        if (Protocol(mode) == "text") { await foreach (var item in sdk.LLM.TextCompletions.StreamAsync((TextCompletionRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } } else if (Protocol(mode) == "responses") { await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } } else { await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)Request(mode), call, token)) { Assert.False(Ending(item)); break; } }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无状态 Responses 拒绝显式存储/关联时仍发送原文，不本地隐藏或改写字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RejectedStatefulInput_IsNotRepaired(bool previous)
    {
        const string mode = "openrouter-responses"; var request = (ResponseRequest)Request(mode); if (previous) request = request with { PreviousResponseId = Part(mode).GetProperty("result").GetProperty("id").GetString() }; else request.Parameters["store"] = JsonSerializer.SerializeToElement(true);
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) =>
        {
            if (previous && ++calls == 1) { Assert.True(JsonElement.DeepEquals(Part(mode).GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false); }
            var expected = JsonNode.Parse(Part(mode).GetProperty("wire").GetRawText())!.AsObject(); if (previous) expected["previous_response_id"] = request.PreviousResponseId; else expected["store"] = true;
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("http_error").GetRawText())), false, status: HttpStatusCode.BadRequest);
        }); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode); if (previous) await Invoke(sdk, mode);
        await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, request: request)); Assert.Equal(previous ? 2 : 1, handler.Calls);
    }
    /// <summary>调用者省略原生 reasoning/usage 时不从模型后缀或网关默认值补入。</summary>
    [Fact]
    public async Task OmittedNativeSettings_StayOmitted()
    {
        const string mode = "openrouter-chat"; var request = (ChatCompletionRequest)Request(mode); request.Parameters.Remove("reasoning"); request.Parameters.Remove("usage"); var target = Target(mode) with { UpstreamModel = "explicit-selected-model-high" };
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part(mode).GetProperty("wire").GetRawText())!.AsObject(); expected.Remove("reasoning"); expected.Remove("usage"); expected["model"] = target.UpstreamModel; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); Assert.False(message.Headers.Contains("HTTP-Referer")); Assert.False(message.Headers.Contains("X-OpenRouter-Title")); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("http_error").GetRawText())), false, status: HttpStatusCode.BadRequest); }); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode, target: target); await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, request: request)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>应用 attribution 头语义变化由调用者更新目标版本或命名客户端，不能复用会话。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitHeaderContextChange_IsBound(bool changeClient)
    {
        const string mode = "openrouter-chat"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var store = CompactionFixture.Store(); var factory = Factory(handler, mode); await using (var first = Build(factory, mode, store: store)) await Invoke(first, mode);
        var changed = Factory(handler, mode, 1); var target = Target(mode); if (!changeClient) target = target with { Version = "attribution-changed" }; await using var second = Build(changed, mode, store: store, target: target, client: changeClient ? "Explicit.ChangedAttribution" : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls); Assert.Empty(changed.Clients);
    }
}
