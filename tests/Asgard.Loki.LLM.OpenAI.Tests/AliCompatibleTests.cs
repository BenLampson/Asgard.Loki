using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式 DashScope 原生 Chat/Responses/Text 复用，保留实际字段并复现已知生成事件的终态边界。</summary>
public sealed class AliCompatibleTests
{
    private static JsonElement Window => Golden("ali-compatible");
    private static JsonElement Part(string mode) => Window.GetProperty(mode);
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static LlmTarget Target(int profile, string mode) => new() { Id = "ali-target", ProviderId = "ali-compatible", BaseAddress = new(Profile(profile).GetProperty("root").GetString() + Window.GetProperty("base_path").GetString()), UpstreamModel = Window.GetProperty("model").GetString()!, CredentialId = "ali-key", Protocols = mode == "chat" ? LlmProtocol.ChatCompletions : mode == "text" ? LlmProtocol.TextCompletions : LlmProtocol.Responses };
    private static ILokiClient Build(OpenAiHttpFactory factory, int profile, string mode, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null) => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, id: "ali-compatible", httpClientName: "Explicit.Ali.Native")], Credentials = [new("ali-key", key ?? Profile(profile).GetProperty("key").GetString()!)], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public", Targets = [target ?? Target(profile, mode)] }] }).Build();
    private static async Task<LlmJsonResult> Invoke(ILokiClient sdk, string mode) => mode switch
    {
        "chat" => await sdk.LLM.ChatCompletions.CompleteAsync(Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken),
        "text" => await sdk.LLM.TextCompletions.CompleteAsync(Part(mode).GetProperty("caller").Deserialize<TextCompletionRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken),
        _ => await sdk.LLM.Responses.CreateAsync(Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken)
    };
    private static async Task Drain(ILokiClient sdk, string mode, List<LlmJsonEvent> seen)
    {
        if (mode == "chat") await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken)) seen.Add(item);
        else if (mode == "text") await foreach (var item in sdk.LLM.TextCompletions.StreamAsync(Part(mode).GetProperty("caller").Deserialize<TextCompletionRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken)) seen.Add(item);
        else await foreach (var item in sdk.LLM.Responses.StreamAsync(Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "ali" }, TestContext.Current.CancellationToken)) seen.Add(item);
    }
    /// <summary>完整旧根、业务空间根、代理前缀均由目标明确选择，原生模型名称不参加协议推断。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1, 2 }) foreach (var mode in new[] { "chat", "responses", "text" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整原生数据和 SSE 元信息逐项核对；top_p/thinking_budget、FIM、tool/call/item ID 不修补。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task NativeProfiles_MatchWholeWindows(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var target = Target(profile, mode);
        if (profile == 2) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+Window.GetProperty("base_path").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.Equal("caller-native-header", Assert.Single(message.Headers.GetValues("X-DashScope-Plugin"))); Assert.Equal(stream, message.Headers.Contains("X-DashScope-SSE"));
            var expected = JsonNode.Parse(part.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token))));
            return Reply(body, stream, Bytes(source).Length);
        }); var factory = new OpenAiHttpFactory(handler) { Configure = c => { c.DefaultRequestHeaders.Add("X-DashScope-Plugin", "caller-native-header"); if (stream) c.DefaultRequestHeaders.Add("X-DashScope-SSE", "enable"); } };
        await using var sdk = Build(factory, profile, mode, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Drain(sdk, mode, seen); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++)
            {
                Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("ali-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds);
                Assert.Equal(mode == "responses" ? expected[i].GetProperty("type").GetString() : null, seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion);
            }
        }
        else { var result = await Invoke(sdk, mode); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("native-reported-ali", result.Data.GetProperty("model").GetString()); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>官方已知推理、自定义工具和搜索生命周期不能在 completed 之后重开。</summary>
    [Theory] [InlineData("response.reasoning_text.delta")] [InlineData("response.reasoning_text.done")]
    [InlineData("response.custom_tool_call_input.delta")] [InlineData("response.custom_tool_call_input.done")]
    [InlineData("response.web_search_call.in_progress")] [InlineData("response.web_search_call.searching")] [InlineData("response.web_search_call.completed")]
    public async Task KnownNativeGenerationAfterTerminal_IsRejected(string type)
    {
        var late = JsonSerializer.Serialize(new { type, response_id = "resp-ali-a", item_id = "late-item", delta = "late", text = "late" }); var body = new OllamaBodyStream(Bytes(Part("responses").GetProperty("sse").GetString()!+Frame(late)));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "responses"); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(sdk, "responses", seen)); Assert.DoesNotContain(seen, e => e.Type == "response.completed"); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知 Chat 业务 type/id/error 不属于 Responses 生命周期，也不能替换原生 Chat ID。</summary>
    [Theory] [InlineData("response-failed")] [InlineData("boolean-type")] [InlineData("business-id")]
    public async Task UnknownChatBusinessMetadata_RemainsOriginal(string variant)
    {
        var trace = variant switch { "response-failed" => "{\"type\":\"response.failed\",\"error\":{\"business\":false}}", "boolean-type" => "{\"type\":false,\"future\":null}", _ => "{\"type\":\"vendor.trace\",\"id\":\"unrelated-business-id\"}" };
        var frames = Part("chat").GetProperty("events").EnumerateArray().Take(4).Select(e => Frame(JsonSerializer.Serialize(e))); var source = string.Concat(frames)+Frame(trace)+Frame("[DONE]");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, 0, "chat"); List<LlmJsonEvent> seen = []; await Drain(sdk, "chat", seen); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(trace), seen[^1].Data)); Assert.Equal(5, seen.Count); Assert.Equal(1, handler.Calls);
    }
    /// <summary>每个原生协议的完整服务器和独立 Key 仍属于 Session，不能静默换业务空间。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)] [InlineData("text", false)] [InlineData("text", true)]
    public async Task NativeTargetDrift_RejectsBeforeSecondSend(string mode, bool keyOnly)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(factory, 0, mode, store)) await Invoke(first, mode);
        await using var second = Build(factory, 0, mode, store, keyOnly ? Target(0, mode) : Target(0, mode) with { BaseAddress = Target(1, mode).BaseAddress }, keyOnly ? Profile(1).GetProperty("key").GetString() : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同配置可重建并按原生 ID 续接 Responses，保留完整 input 和服务地址，不借用公开别名。</summary>
    [Fact]
    public async Task NativeResponsesOwnership_SurvivesInstanceRecreation()
    {
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Target(0, "responses").BaseAddress.AbsoluteUri+"responses", message.RequestUri!.OriginalString); using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(Target(0, "responses").UpstreamModel, data.RootElement.GetProperty("model").GetString()); if (calls++ != 0) Assert.Equal("resp-ali-a", data.RootElement.GetProperty("previous_response_id").GetString()); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(factory, 0, "responses", store)) await Invoke(first, "responses"); await using (var second = Build(factory, 0, "responses", store)) await second.LLM.Responses.CreateAsync(Part("responses").GetProperty("caller").Deserialize<ResponseRequest>()! with { PreviousResponseId = "resp-ali-a" }, new() { SessionId = "ali" }, TestContext.Current.CancellationToken); Assert.Equal(2, handler.Calls);
    }
}
