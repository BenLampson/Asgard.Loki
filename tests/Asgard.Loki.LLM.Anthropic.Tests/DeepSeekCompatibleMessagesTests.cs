using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>原生 DeepSeek Messages 复用已有适配器，不伪造 Claude 签名或把 effort 当 schema 支持。</summary>
public sealed class DeepSeekCompatibleMessagesTests
{
    private static JsonElement Window => Golden("deepseek-compatible");
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory, bool bearer) => new(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey }, id: "deepseek-messages", httpClientName: "Explicit.DeepSeek.Messages");
    private static LlmTarget NativeTarget(AnthropicLlmProvider provider, int profile) => Target(provider, Profile(profile).GetProperty("origin").GetString()+Window.GetProperty("base_path").GetString(), Window.GetProperty("model").GetString()!);
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    /// <summary>原生根和完整代理前缀分别覆盖两种明确认证、完整 JSON 与 SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var bearer in new[] { false, true }) foreach (var stream in new[] { false, true }) yield return [profile, bearer, stream]; }
    /// <summary>工具、远端搜索结果、原生思考/effort、用户字段、未知值和传输身份全部原样保留。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task CompleteNativeMessages_ArePreserved(int profile, bool bearer, bool stream)
    {
        var golden = Window; var source = stream ? golden.GetProperty("sse").GetString()! : golden.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var key = Profile(profile).GetProperty("key").GetString()!;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("origin").GetString()+golden.GetProperty("base_path").GetString()+"messages", message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            if (bearer) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(key, message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key")); } else { Assert.Null(message.Headers.Authorization); Assert.Equal(key, Assert.Single(message.Headers.GetValues("x-api-key"))); }
            var expected = JsonNode.Parse(golden.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory, bearer); var target = NativeTarget(provider, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        await using var sdk = Build(provider, target: target, key: key); var request = Request(); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            List<ClaudeMessageEvent> seen = []; await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken)) { if (item.Type is "message_delta" or "message_stop") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
            var expected = golden.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal(expected[i].GetProperty("type").GetString(), seen[i].EventName); Assert.Equal("deepseek-transport", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await sdk.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(golden.GetProperty("result"), result.Data)); Assert.False(result.Data.GetProperty("content")[0].TryGetProperty("signature", out _)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型后缀不会剥离或覆盖原生思考、budget_tokens、effort 和其他参数。</summary>
    [Fact]
    public async Task NativeModelSelector_DoesNotRewriteThinking()
    {
        const string model = "deepseek-v4-pro-max"; using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Window.GetProperty("wire").GetRawText())!.AsObject(); expected["model"] = model; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new MemoryStream(Bytes(Window.GetProperty("result").GetRawText())), false); }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory, true); await using var sdk = Build(provider, target: NativeTarget(provider, 0) with { UpstreamModel = model }, key: Profile(0).GetProperty("key").GetString()!);
        await sdk.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已闭合搜索/工具/思考块不能替代缺失 message_stop，EOF 不产生成功。</summary>
    [Fact]
    public async Task NativeMessageStop_IsRequired()
    {
        var source = string.Concat(Window.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "message_stop").Select(e => Frame(JsonSerializer.Serialize(e)))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(source)), true))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory, true); await using var sdk = Build(provider, target: NativeTarget(provider, 0), key: Profile(0).GetProperty("key").GetString()!); List<ClaudeMessageEvent> seen = [];
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken)) seen.Add(item); }); Assert.Contains("message_stop", failure.Message); Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同模型名称也不能把已绑定会话迁到另一个完整根或独立凭据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedNativeBinding_FailsBeforeSecondHttp(bool keyOnly)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Window.GetProperty("result").GetRawText())), false))); var factory = new AnthropicHttpFactory(handler); var store = Store(); var provider = Provider(factory, true);
        await using (var first = Build(provider, store, NativeTarget(provider, 0), Profile(0).GetProperty("key").GetString()!)) await first.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken);
        provider = Provider(factory, true); await using var second = Build(provider, store, keyOnly ? NativeTarget(provider, 0) : NativeTarget(provider, 1), Profile(keyOnly ? 1 : 0).GetProperty("key").GetString()!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "deepseek" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
