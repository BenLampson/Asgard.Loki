using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>明确四类原生根和完整历史，无状态 Responses 与各 FIM 参数组合保持原文。</summary>
public sealed class DeepSeekCompatibleTests
{
    private static JsonElement Window => Golden("deepseek-compatible");
    private static JsonElement Part(string mode) => Window.GetProperty(mode);
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static LlmTarget Target(int profile, string mode) => new() { Id = "deepseek-target", ProviderId = "deepseek-native", BaseAddress = new(Profile(profile).GetProperty("origin").GetString()+Part(mode).GetProperty("base_path").GetString()), UpstreamModel = Window.GetProperty("model").GetString()!, CredentialId = "deepseek-key", Protocols = mode == "chat" ? LlmProtocol.ChatCompletions : mode == "responses" ? LlmProtocol.Responses : LlmProtocol.TextCompletions };
    private static ILokiClient Build(OpenAiHttpFactory factory, int profile, string mode, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null) => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, id: "deepseek-native", httpClientName: "Explicit.DeepSeek.Native")], Credentials = [new("deepseek-key", key ?? Profile(profile).GetProperty("key").GetString()!)], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public", Targets = [target ?? Target(profile, mode)] }] }).Build();
    /// <summary>每种请求使用原生完整 caller，调用前后实际请求记录保持不变。</summary>
    private static async Task<LlmJsonResult?> Invoke(ILokiClient sdk, string mode, bool stream = false, List<LlmJsonEvent>? seen = null, Action<LlmJsonEvent>? observe = null)
    {
        var caller = Part(mode).GetProperty(stream ? "caller_stream" : "caller"); object request = mode == "chat" ? caller.Deserialize<ChatCompletionRequest>()! : mode == "responses" ? caller.Deserialize<ResponseRequest>()! : caller.Deserialize<TextCompletionRequest>()!;
        var before = JsonSerializer.Serialize(request); LlmJsonResult? result = null; var call = new LlmCallOptions { SessionId = "deepseek" }; var token = TestContext.Current.CancellationToken;
        void Add(LlmJsonEvent item) { observe?.Invoke(item); seen!.Add(item); }
        if (mode == "chat") { if (stream) await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)request, call, token)) Add(item); else result = await sdk.LLM.ChatCompletions.CompleteAsync((ChatCompletionRequest)request, call, token); }
        else if (mode == "responses") { if (stream) await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)request, call, token)) Add(item); else result = await sdk.LLM.Responses.CreateAsync((ResponseRequest)request, call, token); }
        else { if (stream) await foreach (var item in sdk.LLM.TextCompletions.StreamAsync((TextCompletionRequest)request, call, token)) Add(item); else result = await sdk.LLM.TextCompletions.CompleteAsync((TextCompletionRequest)request, call, token); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); return result;
    }
    /// <summary>官方根与代理前缀分开配置，FIM suffix/logprobs/echo 使用三组互不冲突的原生输入。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var mode in new[] { "chat", "responses", "fim", "fim_logprobs", "fim_echo" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>逐字段比较完整 native wire/result/events；不从 SDK 返回生成快照预期或虚构 DONE。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var target = Target(profile, mode); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("origin").GetString()+part.GetProperty("base_path").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, profile, mode, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item =>
            {
                var ending = mode == "responses" ? item.Type == "response.completed" : item.Data.GetProperty("choices").EnumerateArray().Any(c => c.GetProperty("finish_reason").ValueKind != JsonValueKind.Null);
                if (ending) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            }); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("deepseek-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(mode == "responses" ? expected[i].GetProperty("type").GetString() : null, seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
            if (mode.StartsWith("fim", StringComparison.Ordinal)) { Assert.Equal(2, seen.Count); Assert.Equal(0L, seen[^1].Data.GetProperty("usage").GetProperty("prompt_cache_hit_tokens").GetInt64()); Assert.Single(seen[^1].Data.GetProperty("choices").EnumerateArray()); }
        }
        else { var result = (await Invoke(sdk, mode))!; Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("reported-native-model", result.Data.GetProperty("model").GetString()); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型后缀不能剥离或注入 thinking/effort，更不能改端点协议。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")] [InlineData("fim")]
    public async Task SuffixSelector_DoesNotRewriteNativeRequest(string mode)
    {
        const string model = "deepseek-v4-pro-max"; using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Profile(0).GetProperty("origin").GetString()+Part(mode).GetProperty("base_path").GetString()+Part(mode).GetProperty("path").GetString(), message.RequestUri!.OriginalString); using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(model, data.RootElement.GetProperty("model").GetString()); Assert.False(data.RootElement.TryGetProperty("thinking", out _)); Assert.False(data.RootElement.TryGetProperty("reasoning_effort", out _)); Assert.False(data.RootElement.TryGetProperty("reasoning", out _)); return Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, mode, target: Target(0, mode) with { UpstreamModel = model });
        if (mode == "chat") await sdk.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = [] }, new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken);
        else if (mode == "responses") await sdk.LLM.Responses.CreateAsync(new() { Model = "public", Input = System.Text.Json.Nodes.JsonValue.Create("raw input") }, new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken);
        else await sdk.LLM.TextCompletions.CompleteAsync(new() { Model = "public", Parameters = new() { ["prompt"] = JsonSerializer.SerializeToElement("raw prefix") } }, new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 incomplete/failed 无 DONE，保留原生状态、原因和错误，不补 completed。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeResponsesTerminal_PreservesOutcome(bool failed)
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse(Part("responses").GetProperty("result").GetRawText())!.AsObject(); result["status"] = failed ? "failed" : "incomplete";
        if (failed) result["error"] = System.Text.Json.Nodes.JsonNode.Parse("{\"code\":\"native_failure\",\"message\":\"original\"}"); else result["incomplete_details"] = System.Text.Json.Nodes.JsonNode.Parse("{\"reason\":\"max_output_tokens\"}");
        var terminal = JsonSerializer.Serialize(new { type = failed ? "response.failed" : "response.incomplete", response = result }); var source = string.Concat(Part("responses").GetProperty("events").EnumerateArray().Take(13).Select(e => Frame(JsonSerializer.Serialize(e))))+Frame(terminal);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "responses"); List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(terminal), seen[^1].Data)); Assert.DoesNotContain(seen, e => e.Type == "response.completed"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际原生终态缺失必须失败，不能凭 EOF 或某个 choice 的结束补全整体流。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")] [InlineData("fim")] [InlineData("fim_logprobs")] [InlineData("fim_echo")]
    public async Task MissingNativeTerminal_IsRejected(string mode)
    {
        var source = mode == "responses" ? string.Concat(Part(mode).GetProperty("events").EnumerateArray().Take(13).Select(e => Frame(JsonSerializer.Serialize(e)))) : Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", "");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, mode); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, e => mode == "responses" ? e.Type == "response.completed" : e.Data.GetProperty("choices").EnumerateArray().Any(c => c.GetProperty("finish_reason").ValueKind != JsonValueKind.Null)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无状态 API 的同配置跨实例调用回传完整 input，不自动填写 previous_response_id。</summary>
    [Fact]
    public async Task StatelessResponses_RecreatesWithFullHistory()
    {
        using var handler = new StubHttpHandler(async (message, token) => { using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("responses").GetProperty("wire"), data.RootElement)); Assert.False(data.RootElement.TryGetProperty("previous_response_id", out _)); Assert.False(data.RootElement.TryGetProperty("conversation", out _)); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(factory, 0, "responses", store)) await Invoke(first, "responses"); await using (var second = Build(factory, 0, "responses", store)) await Invoke(second, "responses"); Assert.Equal(2, handler.Calls);
    }
    /// <summary>完整根与独立 Key 是会话身份，即便选中模型相同也不能静默迁移。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)] [InlineData("fim", false)] [InlineData("fim", true)]
    public async Task ChangedNativeBinding_RejectsBeforeSecondSend(string mode, bool keyOnly)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, 0, mode, store)) await Invoke(first, mode);
        await using var second = Build(factory, 0, mode, store, keyOnly ? Target(0, mode) : Target(0, mode) with { BaseAddress = Target(1, mode).BaseAddress }, keyOnly ? Profile(1).GetProperty("key").GetString() : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
}
