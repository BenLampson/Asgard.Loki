using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>原生 Messages 复用现有 Bearer 选项和完整目标，显式本地占位与云端凭据保持区别。</summary>
public sealed class OllamaCompatibleMessagesTests
{
    private static JsonElement Window => Golden("ollama-compatible");
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory) => new(factory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "ollama-messages", httpClientName: "Explicit.Ollama.Messages");
    private static LlmTarget CompatibleTarget(AnthropicLlmProvider provider, int profile) => Target(provider, Profile(profile).GetProperty("root").GetString()!, Window.GetProperty("model").GetString()!) with { Id = Profile(profile).GetProperty("id").GetString()! };
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    /// <summary>完整原生请求/结果及 SSE 身份、关联和 opaque 签名保真；可选字段不代表远端模型支持。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task ExplicitMessagesProfiles_MatchFullNativeWindows(int profile, bool stream)
    {
        var golden = Window; var source = stream ? golden.GetProperty("sse").GetString()! : golden.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+"messages", message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key")); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            var expected = JsonNode.Parse(golden.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); var target = CompatibleTarget(provider, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        await using var sdk = Build(provider, target: target, key: Profile(profile).GetProperty("key").GetString()!); var request = Request(); var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            List<ClaudeMessageEvent> seen = []; await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "compatible" }, TestContext.Current.CancellationToken))
            { if (item.Type is "message_delta" or "message_stop" or "future.metadata") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
            var events = golden.GetProperty("events"); Assert.Equal(events.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(events[i], seen[i].Data)); Assert.Equal(events[i].GetProperty("type").GetString(), seen[i].EventName); Assert.Equal("transport-ollama", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(target.Id, seen[i].Metadata.TargetId); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await sdk.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "compatible" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(golden.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("reported-ollama", result.Data.GetProperty("model").GetString()); }
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(1, handler.Calls); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>工具块已闭合也不能掩盖缺失 message_stop，不从 EOF 伪造成功。</summary>
    [Fact]
    public async Task MissingMessageStop_RejectsNativeTruncation()
    {
        var source = string.Concat(Window.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() != "message_stop").Select(e => Frame(JsonSerializer.Serialize(e)))); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); await using var sdk = Build(provider, target: CompatibleTarget(provider, 0), key: Profile(0).GetProperty("key").GetString()!); List<ClaudeMessageEvent> seen = [];
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(Request(), new() { SessionId = "compatible" }, TestContext.Current.CancellationToken)) seen.Add(item); }); Assert.Contains("message_stop", failure.Message); Assert.DoesNotContain(seen, e => e.Type is "message_delta" or "message_stop"); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同模型名称不能将本地已绑定会话更换成另一个地址或独立云端凭据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ProfileDrift_RejectsBeforeSecondHttp(bool keyOnly)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(Window.GetProperty("result").GetRawText())), false))); var factory = new AnthropicHttpFactory(handler); var store = Store(); var provider = Provider(factory);
        await using (var first = Build(provider, store, CompatibleTarget(provider, 0), Profile(0).GetProperty("key").GetString()!)) await first.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "compatible" }, TestContext.Current.CancellationToken);
        provider = Provider(factory); var target = keyOnly ? CompatibleTarget(provider, 0) : CompatibleTarget(provider, 0) with { BaseAddress = CompatibleTarget(provider, 2).BaseAddress };
        await using var second = Build(provider, store, target, keyOnly ? Profile(2).GetProperty("key").GetString()! : Profile(0).GetProperty("key").GetString()!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(Request(), new() { SessionId = "compatible" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
