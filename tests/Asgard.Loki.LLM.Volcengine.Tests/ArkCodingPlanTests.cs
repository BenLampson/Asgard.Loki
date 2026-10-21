using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>显式 Coding Plan 完整目标复用原生包，不通过别名、主机名或模型后缀改变协议。</summary>
public sealed class ArkCodingPlanTests
{
    private const string CodingKey = "fixture-coding-plan-key";
    private static JsonElement Profile { get { using var doc = JsonDocument.Parse(Text("coding-plan.json")); return doc.RootElement.Clone(); } }
    private static string Value(string key) => Profile.GetProperty(key).GetString()!;
    private static VolcengineLlmProvider Provider(ArkHttpFactory factory, string client = "Explicit.Ark.Coding") => new(factory, id: "ark-coding", httpClientName: client);
    private static LlmTarget CodingTarget() => Target() with { Id = "coding-target", ProviderId = "ark-coding", BaseAddress = new(Value("root")) };
    private static ILokiClient Build(VolcengineLlmProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null, string key = CodingKey)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [new("ark-key", key)], SessionStore = store ?? Store(), Routes = [new() { Model = "public", Targets = [target ?? CodingTarget()] }] }).Build();

    /// <summary>公共 SDK 的完整 JSON/SSE 窗口只替换所选实际模型和方法流标志，保留原生工具、推理及未知字段。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task ExplicitProfile_PreservesFullNativeWindows(bool responses, bool stream, bool trimSlash)
    {
        var source = stream ? Text(responses ? "responses.sse" : "chat.sse") : Part(responses ? "responses_result" : "chat_result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Value(responses ? "responses_endpoint" : "chat_endpoint"), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Value("authorization"), message.Headers.Authorization!.ToString()); Assert.Equal(message.RequestUri.Authority, message.Headers.Host);
            Assert.False(message.Headers.Contains("x-api-key")); var expected = JsonNode.Parse(Part(responses ? "responses_wire" : "chat_wire").GetRawText())!.AsObject(); expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); using var factory = new ArkHttpFactory(handler); var provider = Provider(factory); var target = CodingTarget(); if (trimSlash) target = target with { BaseAddress = new(Value("root").TrimEnd('/')) };
        await using var sdk = Build(provider, target: target); var payload = Payload(responses); payload["model"] = "public"; var original = payload.ToJsonString(); List<LlmJsonEvent> events = []; JsonElement result = default;
        if (responses)
        {
            var request = payload.Deserialize<ResponseRequest>()!;
            if (stream) await foreach (var item in sdk.LLM.Responses.StreamAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken)) events.Add(item);
            else result = (await sdk.LLM.Responses.CreateAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken)).Data;
        }
        else
        {
            var request = payload.Deserialize<ChatCompletionRequest>()!;
            if (stream) await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken)) events.Add(item);
            else result = (await sdk.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken)).Data;
        }
        if (stream)
        {
            var expected = Frames(responses); Assert.Equal(expected.Count, events.Count);
            for (var i = 0; i < events.Count; i++) { Assert.Equal(expected[i], events[i].Data.GetRawText()); Assert.Equal("transport-ark", events[i].EventId); Assert.Equal(0L, events[i].RetryMilliseconds); Assert.Equal("coding-target", events[i].Metadata.TargetId); Assert.Equal(Value("model"), events[i].Metadata.UpstreamModel); Assert.Null(events[i].Metadata.Conversion); }
        }
        else Assert.Equal(Part(responses ? "responses_result" : "chat_result").GetRawText(), result.GetRawText());
        Assert.Equal(original, payload.ToJsonString()); Assert.Equal(1, handler.Calls); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>编码服务的根地址、凭据、实际模型、客户端和版本都是 Session 身份，不凭别名借用其他计划。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("client")] [InlineData("version")]
    public async Task CodingProfileDrift_RejectsBeforeSecondSend(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Part("responses_result").GetRawText())), false))); using var factory = new ArkHttpFactory(handler); var store = Store();
        await using (var first = Build(Provider(factory), store)) await first.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "coding" }, TestContext.Current.CancellationToken);
        var target = change switch { "root" => CodingTarget() with { BaseAddress = new("https://ark.cn-beijing.volces.com/api/v3/") }, "model" => CodingTarget() with { UpstreamModel = "other" }, "version" => CodingTarget() with { Version = "changed" }, _ => CodingTarget() };
        await using var second = Build(Provider(factory, change == "client" ? "other-client" : Value("client")), store, target, change == "key" ? "rotated-key" : CodingKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "coding" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>相同服务完整身份跨实例恢复原生响应 ID，不把编码别名当服务端模型发送。</summary>
    [Fact]
    public async Task SameCodingProfile_RestoresNativeResponseOwnership()
    {
        var calls = 0; using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Value("responses_endpoint"), message.RequestUri!.OriginalString); var data = JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token))!.AsObject(); Assert.Equal(Value("model"), data["model"]!.GetValue<string>()); if (calls++ != 0) Assert.Equal("response-ark-a", data["previous_response_id"]!.GetValue<string>()); return Reply(new MemoryStream(Bytes(Part("responses_result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler); var store = Store();
        await using (var first = Build(Provider(factory), store)) await first.LLM.Responses.CreateAsync(new() { Model = "public" }, new() { SessionId = "coding" }, TestContext.Current.CancellationToken);
        await using (var second = Build(Provider(factory), store)) await second.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = "response-ark-a" }, new() { SessionId = "coding" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, c => Assert.True(c.Disposed));
    }
}
