using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>使用现有 Messages 包和明确 Bearer/完整根配置验证编码服务原生数据，不添加品牌包装。</summary>
public sealed class CodingPlanMessagesTests
{
    private const string CodingKey = "fixture-coding-plan-key";
    private static JsonElement Window => Golden("coding-plan");
    private static string Value(string key) => Window.GetProperty(key).GetString()!;
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory, AnthropicAuthentication auth = AnthropicAuthentication.Bearer, string client = "Explicit.Coding.Messages", string version = "2023-06-01") => new(factory, new AnthropicHttpOptions { Authentication = auth }, id: "coding-messages", httpClientName: client, apiVersion: version);
    private static LlmTarget CodingTarget(AnthropicLlmProvider provider) => Target(provider, Value("root"), Value("model"));
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;

    /// <summary>原始 JSON/逐帧和完整字段清单逐一比较；关联顺序、签名、未知数据及精确整数均不投影。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task FullCodingMessages_AreNative(bool stream, bool trimSlash)
    {
        var golden = Window; var wire = stream ? Value("sse") : golden.GetProperty("complete").GetRawText(); var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Value("endpoint"), message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(Value("authorization"), message.Headers.Authorization!.ToString()); Assert.False(message.Headers.Contains("x-api-key")); Assert.Equal(Value("apiVersion"), Assert.Single(message.Headers.GetValues("anthropic-version")));
            var expected = JsonNode.Parse(golden.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(wire).Length);
        }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); var target = CodingTarget(provider); if (trimSlash) target = target with { BaseAddress = new(Value("root").TrimEnd('/')) };
        await using var sdk = Build(provider, target: target, key: CodingKey); var request = Request(); var original = JsonSerializer.Serialize(request); List<ClaudeMessageEvent> events = [];
        if (stream)
        {
            await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken))
            { if (item.Type is "message_delta" or "message_stop" or "future.metadata") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } events.Add(item); }
            var expected = golden.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), events.Count);
            for (var i = 0; i < events.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], events[i].Data)); Assert.Equal(expected[i].GetProperty("type").GetString(), events[i].EventName); Assert.Equal("transport-coding", events[i].EventId); Assert.Equal(0L, events[i].RetryMilliseconds); Assert.Equal(Value("model"), events[i].Metadata.UpstreamModel); Assert.Null(events[i].Metadata.Conversion); }
        }
        else { var result = await sdk.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "coding" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(golden.GetProperty("complete"), result.Data)); Assert.Equal(Value("model"), result.Metadata.UpstreamModel); Assert.Equal("reported-code-model", result.Data.GetProperty("model").GetString()); }
        Assert.Equal(original, JsonSerializer.Serialize(request)); Assert.Equal(1, handler.Calls); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }

    /// <summary>实际编码计划完整身份不能因更换认证、Key、模型、地址、版本或命名客户端而沿用旧 Session。</summary>
    [Theory] [InlineData("auth")] [InlineData("key")] [InlineData("model")] [InlineData("root")] [InlineData("version")] [InlineData("client")]
    public async Task CodingBindingDrift_RejectsBeforeSecondSend(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Window.GetProperty("complete").GetRawText())), false))); var factory = new AnthropicHttpFactory(handler); var store = Store(); var firstProvider = Provider(factory);
        await using (var first = Build(firstProvider, store, CodingTarget(firstProvider), CodingKey)) await first.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "coding" }, TestContext.Current.CancellationToken);
        var provider = Provider(factory, change == "auth" ? AnthropicAuthentication.ApiKey : AnthropicAuthentication.Bearer, change == "client" ? "other" : "Explicit.Coding.Messages", change == "version" ? "2024-01-01" : "2023-06-01");
        var target = change == "root" ? CodingTarget(provider) with { BaseAddress = new("https://native.invalid/other/v1/") } : change == "model" ? CodingTarget(provider) with { UpstreamModel = "other" } : CodingTarget(provider);
        await using var second = Build(provider, store, target, change == "key" ? "rotated-key" : CodingKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "coding" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>同一完整配置可跨实例继续 JSON/SSE，服务端模型字段不覆盖实际选择绑定。</summary>
    [Fact]
    public async Task SameCodingBinding_RestoresForJsonAndStream()
    {
        var calls = 0; using var handler = new StubHttpHandler((message, _) => { Assert.Equal(Value("endpoint"), message.RequestUri!.OriginalString); Assert.Equal(Value("authorization"), message.Headers.Authorization!.ToString()); var stream = calls++ != 0; return Task.FromResult(Reply(new MemoryStream(Bytes(stream ? Value("sse") : Window.GetProperty("complete").GetRawText())), stream)); }); var factory = new AnthropicHttpFactory(handler); var store = Store();
        var provider = Provider(factory); await using (var first = Build(provider, store, CodingTarget(provider), CodingKey)) await first.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "coding" }, TestContext.Current.CancellationToken);
        provider = Provider(factory); await using (var second = Build(provider, store, CodingTarget(provider), CodingKey)) await foreach (var item in second.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "coding" }, TestContext.Current.CancellationToken)) Assert.Equal(Value("model"), item.Metadata.UpstreamModel);
        Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, c => Assert.True(c.Disposed));
    }

    /// <summary>完整编码窗口之后的缺失/迟到错误及读取/释放/取消故障不会公布原生成功结束。</summary>
    [Theory] [InlineData("missing-stop")] [InlineData("late-error")] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")]
    public async Task NativeCodingTerminal_RemainsGuarded(string fault)
    {
        var golden = Window; var source = Value("sse"); if (fault == "missing-stop") source = string.Concat(golden.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "message_stop").Select(e => Frame(JsonSerializer.Serialize(e))));
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}"; if (fault == "late-error") source += Frame(error);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(source)) { FailAtEof = fault == "read", FailDispose = fault == "dispose", OnEof = fault == "cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: CodingTarget(provider), key: CodingKey); List<ClaudeMessageEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "coding" }, cancel.Token)) seen.Add(item); });
        if (fault == "late-error") Assert.Equal(error, Assert.IsType<LlmUpstreamException>(failure).ResponseBody); else if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure); else if (fault is "read" or "dispose") Assert.IsType<IOException>(failure); else Assert.Contains("message_stop", Assert.IsType<InvalidDataException>(failure).Message);
        Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
