using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.InferencePresetFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>明确服务与协议的完整离线调用窗口，包括原生重复 finish 用量帧。</summary>
public sealed class InferencePresetTests
{
    /// <summary>三个源预设原生协议分别覆盖两个完整根和 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var mode in Modes) foreach (var profile in new[] { 0, 1 }) foreach (var stream in new[] { false, true }) yield return [mode, profile, stream]; }
    /// <summary>逐字段比较完整原文，保留独立凭据、可选调用者头及原始用量。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(string mode, int profile, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes); var target = Target(mode, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var config = Profile(mode, profile); Assert.Equal(config.GetProperty("root").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(config.GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = Factory(handler, mode, profile); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("upstream-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Equal("public", seen[i].Metadata.PublicModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始状态、错误元数据和 UTF-8 正文不转成成功对象。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeHttpError_IsOriginal(bool stream)
    {
        const string mode = "chat"; var source = Window.GetProperty("http_error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: HttpStatusCode.BadRequest))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, stream, [])); Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
