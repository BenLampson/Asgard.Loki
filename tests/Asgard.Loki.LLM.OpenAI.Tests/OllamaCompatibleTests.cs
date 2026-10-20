using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>已有原生包复用实际兼容端点与明确凭据；不将服务器协议支持等同模型参数有效。</summary>
public sealed class OllamaCompatibleTests
{
    private static JsonElement Window => Golden("ollama-compatible");
    private static JsonElement Part(string mode) => Window.GetProperty(mode);
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static OpenAiLlmProvider Provider(OpenAiHttpFactory factory, string client = "Explicit.Ollama.Compatible") => new(factory, id: "ollama-compatible", httpClientName: client);
    private static LlmTarget Target(int profile) => new() { Id = Profile(profile).GetProperty("id").GetString()!, ProviderId = "ollama-compatible", BaseAddress = new(Profile(profile).GetProperty("root").GetString()!), UpstreamModel = Window.GetProperty("model").GetString()!, CredentialId = "compatible-key", Features = LlmTargetFeatures.ResponsesCompaction };
    private static ILokiClient Build(OpenAiLlmProvider provider, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null) => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [new("compatible-key", key ?? Profile(profile).GetProperty("key").GetString()!)], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public", Targets = [target ?? Target(profile)] }] }).Build();
    private static async Task<LlmJsonResult> Invoke(ILokiClient sdk, string mode, string session = "compatible") => mode switch
    {
        "chat" => await sdk.LLM.ChatCompletions.CompleteAsync(Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = session }, TestContext.Current.CancellationToken),
        "responses" => await sdk.LLM.Responses.CreateAsync(Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = session }, TestContext.Current.CancellationToken),
        _ => await sdk.LLM.Responses.CompactAsync(Part(mode).GetProperty("caller").Deserialize<ResponseCompactionRequest>()!, new() { SessionId = session }, TestContext.Current.CancellationToken)
    };
    private static async Task DrainProfile(ILokiClient sdk, string mode, List<LlmJsonEvent> seen, CancellationToken token)
    {
        if (mode == "chat") await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "compatible" }, token)) seen.Add(item);
        else await foreach (var item in sdk.LLM.Responses.StreamAsync(Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!, new() { SessionId = "compatible" }, token)) seen.Add(item);
    }
    /// <summary>本地显式占位 Key、代理与云端独立 Bearer 都验证完整原生窗口，不借用真实服务。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1, 2 }) foreach (var mode in new[] { "chat", "responses", "compact" }) foreach (var stream in mode == "compact" ? new[] { false } : new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整路径、原生模型/方法字段、工具顺序与参数片段、精确值、opaque 压缩和事件元数据逐一保真。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task CompleteNativeProfiles_MatchWholeWindows(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var target = Target(profile);
        if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString() + part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key"));
            var expected = JsonNode.Parse(part.GetProperty("wire").GetRawText())!.AsObject(); if (mode != "compact") expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(Provider(factory), profile, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await DrainProfile(sdk, mode, seen, TestContext.Current.CancellationToken); var events = part.GetProperty("events"); Assert.Equal(events.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(events[i], seen[i].Data)); Assert.Equal("transport-ollama", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(mode == "responses" ? events[i].GetProperty("type").GetString() : null, seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Equal(target.Id, seen[i].Metadata.TargetId); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal(target.Id, result.Metadata.TargetId); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>不完整的实际原生结束和末尾读取故障均不能交付暂存结束；真实 DONE 后仍校验 EOF。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task NativeEnding_IsRequiredThroughEof(string mode, bool readFailure)
    {
        var source = Part(mode).GetProperty("sse").GetString()!;
        if (!readFailure) source = mode == "chat" ? source.Replace("data: [DONE]\n\n", "") : string.Concat(Part(mode).GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "response.completed").Select(e => Frame(JsonSerializer.Serialize(e))));
        var body = new OllamaBodyStream(Bytes(source)) { FailAtEof = readFailure }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(Provider(factory)); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => DrainProfile(sdk, mode, seen, TestContext.Current.CancellationToken)); if (readFailure) Assert.IsType<IOException>(failure); else Assert.IsType<InvalidDataException>(failure);
        Assert.DoesNotContain(seen, e => e.Data.TryGetProperty("type", out var type) && type.GetString() == "response.completed" || e.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>三种 JSON 操作保持原生上游错误，不变成空成功窗口或重试。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")] [InlineData("compact")]
    public async Task NativeError_RemainsOriginal(string mode)
    {
        var raw = Window.GetProperty("error").GetRawText(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(raw)), false))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(Provider(factory)); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode)); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>根地址、实际模型、Key 和客户端参与原有绑定，不能从本地占位配置悄悄切换到另一目标。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("client")]
    public async Task ExplicitBindingDrift_RejectsBeforeSecondSend(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(Provider(factory), store: store)) await Invoke(first, "responses");
        var target = change == "root" ? Target(0) with { BaseAddress = new("https://other.invalid/v1/") } : change == "model" ? Target(0) with { UpstreamModel = "other" } : Target(0);
        await using var second = Build(Provider(factory, change == "client" ? "other-client" : "Explicit.Ollama.Compatible"), store: store, target: target, key: change == "key" ? "other-key" : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, "responses")); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同配置只恢复目标亲和；每次显式发送完整 input，不声称服务端支持 previous_response_id。</summary>
    [Fact]
    public async Task SameProfile_RecreatesWithStatelessInput()
    {
        using var handler = new StubHttpHandler(async (message, token) => { using var data = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part("responses").GetProperty("wire"), data.RootElement)); Assert.False(data.RootElement.TryGetProperty("previous_response_id", out _)); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(Provider(factory), store: store)) await Invoke(first, "responses"); await using (var second = Build(Provider(factory), store: store)) await Invoke(second, "responses"); Assert.Equal(2, handler.Calls);
    }
    /// <summary>compaction 原生 ID 不属于生成响应；拒绝把它用作后续 previous_response_id。</summary>
    [Fact]
    public async Task CompactionId_IsNotGeneratedResponseOwnership()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("compact").GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(Provider(factory)); await Invoke(sdk, "compact");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = "resp-compact-ollama" }, new() { SessionId = "compatible" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
