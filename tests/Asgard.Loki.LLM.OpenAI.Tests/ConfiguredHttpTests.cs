using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.ConfiguredHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>五个原生 HTTP 操作和四种显式认证的完整独立快照，不借用默认服务器或密钥。</summary>
public sealed class ConfiguredHttpTests
{
    /// <summary>压缩与审核为完整 JSON，三个生成协议还支持原生 SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var mode in Modes) foreach (var profile in Enumerable.Range(0, 4)) foreach (var stream in new[] { false, true }) if (!stream || mode is not ("compact" or "moderations")) yield return [mode, profile, stream]; }
    /// <summary>比较完整原生请求、路径、查询、认证、结果及逐帧事件，包括精确小数和独立原生模型。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task CompleteNativeWindows_AreOriginal(string mode, int profile, bool stream)
    {
        var part = Part(mode); var config = Profile(mode, profile); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Window.GetProperty("root").GetString()+part.GetProperty("path").GetString()+config.GetProperty("query_suffix").GetString(), message.RequestUri!.OriginalString); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal(HttpMethod.Post, message.Method);
            foreach (var name in new[] { "Authorization", "x-native-key", "x-api-key", "api-key", "x-goog-api-key" })
            { if (name == config.GetProperty("header").GetString()) Assert.Equal(config.GetProperty("value").GetString(), Assert.Single(message.Headers.GetValues(name))); else Assert.False(message.Headers.Contains(name)); }
            Assert.Equal("kept", Assert.Single(message.Headers.GetValues("x-caller-context")));
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, Bytes(source).Length);
        });
        var factory = new OpenAiHttpFactory(handler) { Configure = client => { client.BaseAddress = new("https://unused.invalid/"); client.DefaultRequestHeaders.Host = "unused.invalid"; client.DefaultRequestHeaders.Add("x-caller-context", "kept"); if (config.GetProperty("header").GetString() is { } header) client.DefaultRequestHeaders.TryAddWithoutValidation(header, "wrong-default"); } };
        await using var sdk = Build(factory, mode, profile); List<LlmJsonEvent> seen = [];
        var result = await Invoke(sdk, mode, stream, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
        if (stream)
        {
            var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else
        {
            Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Null(result.Metadata.Conversion);
            if (mode == "moderations") Assert.Equal("0.123456789012345678901", result.Data.GetProperty("results")[0].GetProperty("category_scores").GetProperty("harassment").GetRawText());
        }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>每种认证跨实例保持完整历史；选中凭据值从不进入供应商公开身份。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task SameBinding_ReplaysOriginalHistory(int profile)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part("chat").GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("chat").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, "chat", profile, store); await Invoke(sdk, "chat"); }
        var provider = new OpenAiLlmProvider(factory, new OpenAiHttpOptions { Transport = Options(profile) }); if (profile != 1) Assert.DoesNotContain(Profile("chat", profile).GetProperty("key").GetString()!, provider.BindingIdentity); Assert.Equal(2, handler.Calls);
    }
}
