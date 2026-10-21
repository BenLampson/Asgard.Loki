using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>已配置上游的原生 Messages 保留自身 schema、思考、工具和缓存计数含义。</summary>
public sealed class InferencePresetMessagesTests
{
    private static JsonElement Window => Golden("inference-presets");
    private static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    private static AnthropicLlmProvider Provider(AnthropicHttpFactory factory) => new(factory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "inference-preset-messages", httpClientName: "Explicit.Upstream.Messages");
    private static LlmTarget NativeTarget(AnthropicLlmProvider provider, int profile) => Target(provider, Profile(profile).GetProperty("root").GetString()!, Window.GetProperty("model").GetString()!) with { Version = Profile(profile).GetProperty("version").GetString()! };
    private static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    /// <summary>原生与代理完整根分别覆盖明确认证和原生 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in Enumerable.Range(0, 2)) foreach (var stream in new[] { false, true }) yield return [profile, stream]; }
    /// <summary>逐项比较完整请求、结果与事件，缓存计数不归一化，不执行原生工具。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeMessagesWindows_ArePreserved(int profile, bool stream)
    {
        var source = stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); var key = Profile(profile).GetProperty("key").GetString()!;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+"messages", message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(key, message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key"));
            var expected = JsonNode.Parse(Window.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new AnthropicHttpFactory(handler); var provider = Provider(factory); var target = NativeTarget(provider, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
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
}
