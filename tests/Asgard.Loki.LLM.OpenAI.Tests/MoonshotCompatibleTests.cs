using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生 Kimi Chat、Partial 和 Responses 完整窗口，不猜测旧文本接口可用性。</summary>
public sealed class MoonshotCompatibleTests
{
    private static JsonElement Window => Golden("moonshot-compatible");
    private static JsonElement Part(string mode) => Window.GetProperty(mode);
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static LlmTarget Target(int profile, string mode) => new() { Id = "moonshot-target", ProviderId = "moonshot-native", BaseAddress = new(Profile(profile).GetProperty("root").GetString()!), UpstreamModel = Window.GetProperty("model").GetString()!, CredentialId = "moonshot-key", Protocols = mode == "responses" ? LlmProtocol.Responses : LlmProtocol.ChatCompletions };
    private static ILokiClient Build(OpenAiHttpFactory factory, int profile, string mode, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null, string client = "Explicit.Moonshot.Native", OpenAiHttpOptions? options = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, options ?? new(), id: "moonshot-native", httpClientName: client)], Credentials = [new("moonshot-key", key ?? Profile(profile).GetProperty("key").GetString()!)], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public", Targets = [target ?? Target(profile, mode)] }] }).Build();
    /// <summary>原生调用使用完整独立 caller 并验证调用者快照不被修改。</summary>
    private static async Task<LlmJsonResult?> Invoke(ILokiClient sdk, string mode, bool stream = false, List<LlmJsonEvent>? seen = null, Action<LlmJsonEvent>? observe = null)
    {
        var caller = Part(mode).GetProperty(stream ? "caller_stream" : "caller"); object request = mode == "responses" ? caller.Deserialize<ResponseRequest>()! : caller.Deserialize<ChatCompletionRequest>()!;
        var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "moonshot" }; var token = TestContext.Current.CancellationToken; LlmJsonResult? result = null;
        void Add(LlmJsonEvent item) { observe?.Invoke(item); seen!.Add(item); }
        if (mode == "responses") { if (stream) await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)request, call, token)) Add(item); else result = await sdk.LLM.Responses.CreateAsync((ResponseRequest)request, call, token); }
        else { if (stream) await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)request, call, token)) Add(item); else result = await sdk.LLM.ChatCompletions.CompleteAsync((ChatCompletionRequest)request, call, token); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); return result;
    }
    /// <summary>平台、区域、Coding 与代理均为独立完整根和凭据，不共享隐式目标。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in Enumerable.Range(0, 5)) foreach (var mode in new[] { "chat", "partial", "responses" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整 JSON/SSE 对照保留原生工具、推理摘要、缓存、schema 与未知字段。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var target = Target(profile, mode); if (profile == 4) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            var data = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), data));
            if (mode == "partial") { Assert.True(data.GetProperty("messages")[1].GetProperty("partial").GetBoolean()); Assert.False(data.TryGetProperty("partial", out _)); Assert.False(data.TryGetProperty("response_format", out _)); }
            return Reply(body, stream, Bytes(source).Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, profile, mode, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item =>
            {
                if (mode == "responses" ? item.Type == "response.completed" : item.Data.GetProperty("choices").EnumerateArray().Any(c => c.GetProperty("finish_reason").ValueKind != JsonValueKind.Null)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            }); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("moonshot-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(mode == "responses" ? expected[i].GetProperty("type").GetString() : null, seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = (await Invoke(sdk, mode))!; Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("reported-native-model", result.Data.GetProperty("model").GetString()); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>明确的推理摘要生命周期不能在原生 completed 之后重开。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task KnownSummaryAfterTerminal_IsRejected(int index)
    {
        var late = Window.GetProperty("late_summary_events")[index]; var body = new OllamaBodyStream(Bytes(Part("responses").GetProperty("sse").GetString()!+Frame(JsonSerializer.Serialize(late)))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "responses"); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, "responses", true, seen)); Assert.DoesNotContain(seen, e => e.Type == "response.completed"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知的相似名称仍是业务数据，不由前缀猜测生成或注册响应身份。</summary>
    [Fact]
    public async Task UnknownSummaryMetadata_RemainsOriginal()
    {
        const string unknown = "{\"type\":\"response.reasoning_summary_text.future\",\"response\":{\"id\":\"business-other\",\"status\":false},\"error\":{\"business\":false},\"future\":9007199254740993}";
        var body = new OllamaBodyStream(Bytes(Part("responses").GetProperty("sse").GetString()!+Frame(unknown))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "responses"); List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(unknown), seen[^1].Data)); Assert.True(body.Eof); Assert.Equal(17, seen.Count); Assert.Equal(1, handler.Calls);
    }
    /// <summary>三种窗口的原生终态均不可由 EOF 或局部内容完成代替。</summary>
    [Theory] [InlineData("chat")] [InlineData("partial")] [InlineData("responses")]
    public async Task MissingNativeTerminal_IsRejected(string mode)
    {
        var part = Part(mode); var source = mode == "responses" ? string.Concat(part.GetProperty("events").EnumerateArray().Take(15).Select(e => Frame(JsonSerializer.Serialize(e)))) : part.GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", "");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, mode); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, e => mode == "responses" ? e.Type == "response.completed" : e.Data.GetProperty("choices").EnumerateArray().Any(c => c.GetProperty("finish_reason").ValueKind != JsonValueKind.Null)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生失败和截断保留完整快照，不凭无 DONE 改写结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeResponsesOutcome_IsPreserved(bool failed)
    {
        var result = JsonNode.Parse(Part("responses").GetProperty("result").GetRawText())!.AsObject(); result["status"] = failed ? "failed" : "incomplete";
        if (failed) result["error"] = JsonNode.Parse("{\"type\":\"native_failure\",\"message\":\"original\"}"); else result["incomplete_details"] = JsonNode.Parse("{\"reason\":\"max_output_tokens\"}");
        var terminal = JsonSerializer.Serialize(new { type = failed ? "response.failed" : "response.incomplete", sequence_number = 15, response = result }); var source = string.Concat(Part("responses").GetProperty("events").EnumerateArray().Take(15).Select(e => Frame(JsonSerializer.Serialize(e))))+Frame(terminal);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "responses"); List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(terminal), seen[^1].Data)); Assert.DoesNotContain(seen, e => e.Type == "response.completed"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同会话的实例重建仍发送完整 input，不填写服务端不支持的历史句柄。</summary>
    [Fact]
    public async Task StatelessResponses_ReplaysFullInput()
    {
        using var handler = new StubHttpHandler(async (message, token) => { using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("responses").GetProperty("wire"), data.RootElement)); Assert.False(data.RootElement.TryGetProperty("previous_response_id", out _)); Assert.False(data.RootElement.TryGetProperty("conversation", out _)); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(factory, 0, "responses", store)) await Invoke(first, "responses"); await using (var second = Build(factory, 0, "responses", store)) await Invoke(second, "responses"); Assert.Equal(2, handler.Calls);
    }
    /// <summary>完整配置变化必须在第二次 HTTP 前阻止会话迁移。</summary>
    public static IEnumerable<object[]> DriftCases()
    { foreach (var mode in new[] { "chat", "responses" }) foreach (var change in new[] { "root", "key", "model", "client", "policy" }) yield return [mode, change]; }
    /// <summary>同一公开别名和凭据 ID 不能掩盖服务器、Key、模型及适配实现的变化。</summary>
    [Theory] [MemberData(nameof(DriftCases))]
    public async Task CompleteBindingDrift_IsRejected(string mode, string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, 0, mode, store)) await Invoke(first, mode);
        var target = Target(0, mode); if (change == "root") target = target with { BaseAddress = Target(2, mode).BaseAddress }; if (change == "model") target = target with { UpstreamModel = "another-model" };
        await using var second = Build(factory, 0, mode, store, target, change == "key" ? Profile(2).GetProperty("key").GetString() : null, change == "client" ? "Explicit.Changed" : "Explicit.Moonshot.Native", change == "policy" ? new() { MaximumBodyBytes = 4096 } : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>不会照搬中转站的特定模型 temperature 修补或删除显式 thinking。</summary>
    [Fact]
    public async Task SelectedModel_DoesNotRepairParameters()
    {
        const string caller = "{\"model\":\"public\",\"messages\":[],\"temperature\":0.125,\"thinking\":{\"type\":\"disabled\"}}"; using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(caller)!.AsObject(); expected["model"] = "kimi-k2.6"; expected["stream"] = false; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("chat").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "chat", target: Target(0, "chat") with { UpstreamModel = "kimi-k2.6" });
        await sdk.LLM.ChatCompletions.CompleteAsync(JsonSerializer.Deserialize<ChatCompletionRequest>(caller)!, new() { SessionId = "moonshot" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
}
