using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Google.Tests.ConfiguredGeminiFixture;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>实际 SDK 完整原生窗口覆盖五种认证，保留模型路径、思考签名和稀疏候选关联。</summary>
public sealed class ConfiguredGeminiTests
{
    /// <summary>五种明确凭据位置分别覆盖完整 JSON 和原生 SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in Enumerable.Range(0, 5)) foreach (var stream in new[] { false, true }) yield return [profile, stream]; }
    /// <summary>完整请求/结果/逐帧原文与独立样本比较，模型和密钥各转义一次。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task CompleteNativeWindows_ArePreserved(int profile, bool stream)
    {
        var config = Profile(profile); var source = stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Window.GetProperty("root").GetString()+Window.GetProperty(stream ? "stream_path" : "path").GetString()+config.GetProperty("query_suffix").GetString(), message.RequestUri!.OriginalString); Assert.Equal("configured.invalid", message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method);
            foreach (var name in new[] { "Authorization", "x-goog-api-key", "x-api-key", "x-native-key", "api-key" }) { if (name == config.GetProperty("header").GetString()) Assert.Equal(config.GetProperty("value").GetString(), Assert.Single(message.Headers.NonValidated[name])); else Assert.False(message.Headers.Contains(name)); }
            Assert.Equal("kept", Assert.Single(message.Headers.GetValues("x-caller-context"))); var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), actual)); Assert.False(actual.TryGetProperty("model", out _)); Assert.False(actual.TryGetProperty("stream", out _)); return Reply(body, stream, length: Bytes(source).Length);
        }); var factory = new GoogleHttpFactory(handler) { Configure = client => { client.BaseAddress = new("https://unused.invalid/"); client.DefaultRequestHeaders.Host = "unused.invalid"; client.DefaultRequestHeaders.Add("x-caller-context", "kept"); if (config.GetProperty("header").GetString() is { } name) client.DefaultRequestHeaders.TryAddWithoutValidation(name, "wrong-default"); } }; await using var sdk = Build(Provider(factory, profile), profile); List<GeminiContentEvent> seen = [];
        var result = await Invoke(sdk, stream, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
        if (stream) { var expected = Window.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count); for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(Window.GetProperty("model").GetString(), seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); } }
        else { Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(Window.GetProperty("result"), result.Data)); Assert.Equal(Window.GetProperty("model").GetString(), result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>固定模型路径必须明确声明目标模型，不能把动态模型静默改成服务器默认值。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitFixedModelEndpoint_PreservesNativeWindow(bool stream)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal("https://configured.invalid/native/api/"+(stream ? "fixed:stream?alt=sse" : "fixed:generate?existing=1"), message.RequestUri!.OriginalString); Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(stream ? Window.GetProperty("sse").GetString()! : Window.GetProperty("result").GetRawText())), stream); }); var factory = new GoogleHttpFactory(handler); var options = Options(); options = options with { Transport = options.Transport! with { FixedUpstreamModel = Window.GetProperty("model").GetString(), GenerateContentPath = "fixed:generate?existing=1", StreamGenerateContentPath = "fixed:stream?alt=sse" } }; await using var sdk = Build(Provider(factory, options: options)); List<GeminiContentEvent> seen = []; var result = await Invoke(sdk, stream, seen); if (stream) Assert.Equal(Window.GetProperty("events").GetArrayLength(), seen.Count); else Assert.True(JsonElement.DeepEquals(Window.GetProperty("result"), result!.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同身份跨实例发送完整原生历史，公开绑定身份中不包含密钥值。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SameBinding_ReplaysOriginalHistory(int profile)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Window.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("result").GetRawText()))); }); var factory = new GoogleHttpFactory(handler); var store = Store(); for (var i = 0; i < 2; i++) { await using var sdk = Build(Provider(factory, profile), profile, store); await Invoke(sdk); } if (profile != 2) Assert.DoesNotContain(Profile(profile).GetProperty("key").GetString()!, Provider(factory, profile).BindingIdentity); Assert.Equal(2, handler.Calls);
    }
}
