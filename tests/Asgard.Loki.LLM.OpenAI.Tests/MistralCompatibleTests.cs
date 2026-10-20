using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式原生 Mistral Chat 保留类型化思考、图片容器、工具标识与原生参数。</summary>
public sealed class MistralCompatibleTests
{
    private static JsonElement Window => Golden("mistral-compatible");
    private static JsonElement Part(string mode) => Window.GetProperty(mode);
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static LlmTarget Target(int profile, string mode) => new() { Id = "mistral-target", ProviderId = "mistral-native", BaseAddress = new(Profile(profile).GetProperty("root").GetString()!), UpstreamModel = Part(mode).GetProperty("model").GetString()!, CredentialId = "mistral-key", Protocols = LlmProtocol.ChatCompletions };
    private static ILokiClient Build(OpenAiHttpFactory factory, string mode, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null, string client = "Explicit.Mistral.Native", OpenAiHttpOptions? options = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, options ?? new(), id: "mistral-native", httpClientName: client)], SessionStore = store ?? CompactionFixture.Store(), Credentials = [new("mistral-key", key ?? Profile(profile).GetProperty("key").GetString()!)], Routes = [new() { Model = "public", Targets = [target ?? Target(profile, mode)] }] }).Build();
    private static ChatCompletionRequest Request(string mode) => Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!;
    private static async Task Drain(ILokiClient sdk, string mode, List<ChatCompletionEvent> seen, Action<ChatCompletionEvent>? observe = null, ChatCompletionRequest? request = null)
    { await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request ?? Request(mode), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken)) { observe?.Invoke(item); seen.Add(item); } }
    private static bool Ending(ChatCompletionEvent item) => item.Data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(choice => choice.GetProperty("finish_reason").ValueKind != JsonValueKind.Null);
    /// <summary>原生与代理完整根分别覆盖思考、工具、前缀和 JSON 模式。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var mode in new[] { "reasoning", "tools", "prefix", "json" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整 wire/result/events 对照确保原生载荷不被截取、重新编号或转换为其他协议。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var target = Target(profile, mode); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Profile(profile).GetProperty("root").GetString()+"chat/completions", message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter); Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile, target: target); var request = Request(mode); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            List<ChatCompletionEvent> seen = []; await Drain(sdk, mode, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }, request); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("mistral-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
            if (mode == "reasoning") { Assert.Equal(JsonValueKind.Array, seen[0].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").ValueKind); Assert.Equal(2, seen[1].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetArrayLength()); Assert.Equal(JsonValueKind.String, seen[2].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").ValueKind); }
        }
        else { var result = await sdk.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "mistral" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal("reported-native-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非标准工具标识及关联结果保持调用者原文，不偷偷改写为另一个标识。</summary>
    [Fact]
    public async Task NonstandardCallerToolId_IsNotRemapped()
    {
        var caller = JsonNode.Parse(Part("tools").GetProperty("caller").GetRawText())!.AsObject(); caller["messages"]![0]!["tool_calls"]![0]!["id"] = "caller-owned.nonstandard-id"; caller["messages"]![2]!["tool_call_id"] = "caller-owned.nonstandard-id";
        using var handler = new StubHttpHandler(async (message, token) => { var expected = caller.DeepClone().AsObject(); expected["model"] = Target(0, "tools").UpstreamModel; expected["stream"] = false; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("tools").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "tools"); var request = JsonSerializer.Deserialize<ChatCompletionRequest>(caller)!; await sdk.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "mistral" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已声明与未来参数同时保留，不选择性重建请求或重命名 token 参数。</summary>
    [Fact]
    public async Task CallerFields_AreNotRepairedOrDropped()
    {
        var caller = JsonNode.Parse(Part("json").GetProperty("caller").GetRawText())!.AsObject(); caller["max_completion_tokens"] = 1024; caller["future_alias"] = JsonNode.Parse("{\"mode\":false,\"value\":9007199254740993}");
        using var handler = new StubHttpHandler(async (message, token) => { var expected = caller.DeepClone().AsObject(); expected["model"] = Target(0, "json").UpstreamModel; expected["stream"] = false; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("json").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "json"); await sdk.LLM.ChatCompletions.CompleteAsync(JsonSerializer.Deserialize<ChatCompletionRequest>(caller)!, new() { SessionId = "mistral" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整绑定身份的每项语义变更都需新会话。</summary>
    public static IEnumerable<object[]> DriftCases()
    { foreach (var mode in new[] { "reasoning", "tools" }) foreach (var field in new[] { "root", "key", "model", "client", "policy", "version" }) yield return [mode, field]; }
    /// <summary>公开别名相同不能掩盖上游服务器、实际模型、凭据和实现的变化。</summary>
    [Theory] [MemberData(nameof(DriftCases))]
    public async Task CompleteBindingDrift_IsRejected(string mode, string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, store: store)) await first.LLM.ChatCompletions.CompleteAsync(Request(mode), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken);
        var target = Target(0, mode); target = field switch { "root" => target with { BaseAddress = Target(1, mode).BaseAddress }, "model" => target with { UpstreamModel = "another-model" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(factory, mode, store: store, target: target, key: field == "key" ? Profile(1).GetProperty("key").GetString() : null, client: field == "client" ? "Explicit.Changed" : "Explicit.Mistral.Native", options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ChatCompletions.CompleteAsync(Request(mode), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同会话的实例重建仍明确发送原生历史，不在 SDK 内保存工具或模型对话状态。</summary>
    [Fact]
    public async Task SameBinding_RecreatesWithWholeHistory()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part("tools").GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("tools").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, "tools", store: store); await sdk.LLM.ChatCompletions.CompleteAsync(Request("tools"), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>每个窗口都必须有实际 DONE，不能把 finish 或正常 EOF 当成它。</summary>
    [Theory] [InlineData("reasoning")] [InlineData("tools")] [InlineData("prefix")] [InlineData("json")]
    public async Task MissingDone_DiscardsSuccessfulEnding(string mode)
    {
        var source = Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", ""); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Drain(sdk, mode, seen)); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 length/error 结束原因保持原文，不变成 stop 或本地合成成功标签。</summary>
    [Theory] [InlineData("length")] [InlineData("error")]
    public async Task NativeFinishOutcome_IsPreserved(string reason)
    {
        var events = Part("json").GetProperty("events").EnumerateArray().Select(e => JsonNode.Parse(e.GetRawText())!).ToArray(); events[^1]["choices"]![0]!["finish_reason"] = reason; var source = string.Concat(events.Select(e => Frame(JsonSerializer.Serialize(e))))+Frame("[DONE]"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "json"); List<ChatCompletionEvent> seen = []; await Drain(sdk, "json", seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(events[^1]), seen[^1].Data)); Assert.Equal(reason, seen[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 HTTP 错误不被转换成空结果或重试，原始状态/正文保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeHttpError_IsPreserved(bool stream)
    {
        var text = Window.GetProperty("error").GetRawText(); var body = new OllamaBodyStream(Bytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: HttpStatusCode.BadRequest))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "tools");
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (stream) await Drain(sdk, "tools", []); else await sdk.LLM.ChatCompletions.CompleteAsync(Request("tools"), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken); }); Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode); Assert.Equal(text, failure.ResponseBody); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>结束标志之后的底层读取失败仍撤回成功尾部。</summary>
    [Fact]
    public async Task LateReadFailure_DiscardsEnding()
    {
        var body = new OllamaBodyStream(Bytes(Part("reasoning").GetProperty("sse").GetString()!)) { FailAtEof = true }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "reasoning"); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<IOException>(() => Drain(sdk, "reasoning", seen)); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提前退出只关闭已拥有调用，不把已交付的 thinking 分块当完整结果。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesOwnedCall()
    {
        var body = new OllamaBodyStream(Bytes(Part("reasoning").GetProperty("sse").GetString()!)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "reasoning"); await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(Request("reasoning"), new() { SessionId = "mistral" }, TestContext.Current.CancellationToken)) { Assert.False(Ending(item)); break; } Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>DONE 只能出现一次，不能通过后续元数据或另一次结束悄悄绕过。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PostDoneData_IsRejected(bool duplicate)
    {
        var source = Part("json").GetProperty("sse").GetString()!+Frame(duplicate ? "[DONE]" : "{\"type\":\"vendor.trace\",\"future\":false}"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "json"); List<ChatCompletionEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Drain(sdk, "json", seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>未知类型和业务 ID 不能覆盖 Chat 生成身份或被误当 Responses 失败。</summary>
    [Fact]
    public async Task UnknownMetadata_RemainsOriginal()
    {
        const string metadata = "{\"type\":\"response.failed\",\"id\":\"business-other\",\"error\":{\"business\":false},\"future\":9007199254740993}"; var source = Part("reasoning").GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", Frame(metadata)+Frame("[DONE]")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "reasoning"); List<ChatCompletionEvent> seen = []; await Drain(sdk, "reasoning", seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(metadata), seen[^1].Data)); Assert.Equal(5, seen.Count);
    }
}
