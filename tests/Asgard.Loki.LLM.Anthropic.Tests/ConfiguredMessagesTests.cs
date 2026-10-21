using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;
using static Asgard.Loki.LLM.Anthropic.Tests.ConfiguredMessagesFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>五种明确认证均通过实际 SDK 保留完整原生报文、工具历史和终态。</summary>
public sealed class ConfiguredMessagesTests
{
    /// <summary>五个独立凭据位置分别覆盖完整 JSON 与原生 SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in Enumerable.Range(0, 5)) foreach (var stream in new[] { false, true }) yield return [profile, stream]; }
    /// <summary>完整正文与事件不转换；选中 Host、版本和认证在工厂默认值之前明确。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, bool stream)
    {
        var config = Profile(profile); var source = stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Window.GetProperty("root").GetString()+Window.GetProperty("path").GetString()+config.GetProperty("query_suffix").GetString(), message.RequestUri!.OriginalString); Assert.Equal("configured.invalid", message.Headers.Host); Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version"))); Assert.Equal(HttpMethod.Post, message.Method);
            foreach (var name in new[] { "Authorization", "x-api-key", "x-native-key", "x-goog-api-key", "api-key" }) { if (name == config.GetProperty("header").GetString()) Assert.Equal(config.GetProperty("value").GetString(), Assert.Single(message.Headers.NonValidated[name])); else Assert.False(message.Headers.Contains(name)); }
            Assert.Equal("kept", Assert.Single(message.Headers.GetValues("x-caller-context"))); var expected = JsonNode.Parse(Window.GetProperty("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        }); var factory = new AnthropicHttpFactory(handler) { Defaults = client => { client.BaseAddress = new("https://unused.invalid/"); client.DefaultRequestHeaders.Host = "unused.invalid"; client.DefaultRequestHeaders.Add("x-caller-context", "kept"); client.DefaultRequestHeaders.Add("anthropic-version", "wrong-default"); if (config.GetProperty("header").GetString() is { } name) client.DefaultRequestHeaders.TryAddWithoutValidation(name, "wrong-default"); } }; var provider = Provider(factory, profile); await using var sdk = Build(provider, profile); List<ClaudeMessageEvent> seen = [];
        var result = await Invoke(sdk, stream, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
        if (stream) { var expected = Window.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count); for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Null(seen[i].Metadata.Conversion); Assert.Equal(Window.GetProperty("model").GetString(), seen[i].Metadata.UpstreamModel); } }
        else { Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(Window.GetProperty("result"), result.Data)); Assert.Null(result.Metadata.Conversion); Assert.False(result.Data.GetProperty("content")[0].TryGetProperty("signature", out _)); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同一个完整目标跨实例重放原始历史，不额外拼接工具或思考内容。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SameBinding_ReplaysOriginalHistory(int profile)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText())), false); }); var factory = new AnthropicHttpFactory(handler); var store = Store();
        for (var i = 0; i < 2; i++) { var provider = Provider(factory, profile); await using var sdk = Build(provider, profile, store); await Invoke(sdk); } if (profile != 2) Assert.DoesNotContain(Profile(profile).GetProperty("key").GetString()!, Provider(factory, profile).BindingIdentity); Assert.Equal(2, handler.Calls);
    }
}
