using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>已配置上游的原生 Messages 保留自身 schema、思考、工具和缓存计数含义。</summary>
public sealed class UpstreamMessagesTests
{
    private static JsonElement Window => Golden("upstream-native");
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory, bool bearer = true, string client = "Explicit.Upstream.Messages", int maximum = 32 * 1024 * 1024) => new(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey, MaximumBodyBytes = maximum }, id: "native-upstream-messages", httpClientName: client);
    private static LlmTarget NativeTarget(AnthropicLlmProvider provider, int profile) => Target(provider, Profile(profile).GetProperty("root").GetString()!, Window.GetProperty("model").GetString()!) with { Version = Profile(profile).GetProperty("version").GetString()! };
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    /// <summary>原生与代理完整根分别覆盖明确认证和原生 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in Enumerable.Range(0, 3)) foreach (var bearer in new[] { false, true }) foreach (var stream in new[] { false, true }) yield return [profile, bearer, stream]; }
    /// <summary>逐项比较完整请求、结果与事件，缓存计数不归一化，不执行原生工具。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeMessagesWindows_ArePreserved(int profile, bool bearer, bool stream)
    {
        var source = stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var key = Profile(profile).GetProperty("key").GetString()!;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+"messages", message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            if (bearer) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(key, message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key")); } else { Assert.Null(message.Headers.Authorization); Assert.Equal(key, Assert.Single(message.Headers.GetValues("x-api-key"))); }
            var expected = JsonNode.Parse(Window.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory, bearer); var target = NativeTarget(provider, profile); if (profile == 2) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        await using var sdk = Build(provider, target: target, key: key); var request = Request(); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            List<ClaudeMessageEvent> seen = []; await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken)) { if (item.Type is "message_delta" or "message_stop") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
            var expected = Window.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Null(seen[i].EventName); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await sdk.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(Window.GetProperty("result"), result.Data)); Assert.Equal(41L, result.Data.GetProperty("usage").GetProperty("input_tokens").GetInt64()); Assert.False(result.Data.GetProperty("content")[0].TryGetProperty("signature", out _)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>上游省略签名时保持省略，不从其他供应商补造签名字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AbsentNativeSignature_IsNotInvented(bool stream)
    {
        var result = JsonNode.Parse(Window.GetProperty("result").GetRawText())!; result["content"]![0]!.AsObject().Remove("signature");
        var events = Window.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "content_block_delta" || e.GetProperty("delta").GetProperty("type").GetString() != "signature_delta").ToArray(); var source = stream ? string.Concat(events.Select(e => Frame(JsonSerializer.Serialize(e)))) : result.ToJsonString();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), stream))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: NativeTarget(provider, 0), key: Profile(0).GetProperty("key").GetString()!);
        if (stream) { List<ClaudeMessageEvent> seen = []; await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken)) seen.Add(item); Assert.Equal(events.Length, seen.Count); for (var i = 0; i < events.Length; i++) Assert.True(JsonElement.DeepEquals(events[i], seen[i].Data)); }
        else { var actual = await sdk.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken); Assert.False(actual.Data.GetProperty("content")[0].TryGetProperty("signature", out _)); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(result.ToJsonString()), actual.Data)); }
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>本地已接收全部工具内容也不能替代原生 message_stop。</summary>
    [Fact]
    public async Task MissingMessageStop_IsRejected()
    {
        var source = string.Concat(Window.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "message_stop").Select(e => Frame(JsonSerializer.Serialize(e)))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: NativeTarget(provider, 0), key: Profile(0).GetProperty("key").GetString()!); List<ClaudeMessageEvent> seen = [];
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken)) seen.Add(item); }); Assert.Contains("message_stop", failure.Message); Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整根、Key、模型和认证/命名客户端仍属于固定会话身份。</summary>
    [Theory] [InlineData("root")] [InlineData("key")] [InlineData("model")] [InlineData("auth")] [InlineData("client")] [InlineData("version")] [InlineData("policy")]
    public async Task ChangedCompleteBinding_IsRejected(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false))); var factory = new AnthropicHttpFactory(handler); var store = Store(); var provider = Provider(factory);
        await using (var first = Build(provider, store, NativeTarget(provider, 0), Profile(0).GetProperty("key").GetString()!)) await first.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken);
        provider = Provider(factory, change != "auth", change == "client" ? "Explicit.Changed" : "Explicit.Upstream.Messages", maximum: change == "policy" ? 8192 : 32 * 1024 * 1024); var target = NativeTarget(provider, change == "root" ? 1 : 0); if (change == "model") target = target with { UpstreamModel = "changed-model" }; if (change == "version") target = target with { Version = "changed-version" };
        await using var second = Build(provider, store, target, Profile(change == "key" ? 1 : 0).GetProperty("key").GetString()!); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "native-upstream" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
