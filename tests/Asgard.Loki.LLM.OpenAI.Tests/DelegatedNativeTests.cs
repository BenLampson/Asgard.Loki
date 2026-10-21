using System.Net;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.DelegatedNativeFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>明确服务与协议的完整离线调用窗口，包括原生重复 finish 用量帧。</summary>
public sealed class DelegatedNativeTests
{
    /// <summary>七个实际协议配置各自覆盖两个完整根和 JSON/SSE。</summary>
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
            foreach (var (field, name) in new[] { ("referer", "HTTP-Referer"), ("title", "X-OpenRouter-Title") }) { if (config.GetProperty(field).ValueKind == JsonValueKind.Null) Assert.False(message.Headers.Contains(name)); else Assert.Equal(config.GetProperty(field).GetString(), Assert.Single(message.Headers.GetValues(name))); }
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = Factory(handler, mode, profile); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("delegated-native", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Equal("public", seen[i].Metadata.PublicModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal("reported-native-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Null(result.Metadata.Conversion); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>每个完整原生配置跨实例重放调用者原始历史或 prompt。</summary>
    public static IEnumerable<object[]> NativeModes() => Modes.Select(mode => new object[] { mode });
    /// <summary>会话缓存不追加隐藏历史，也不把文本 prompt 转成消息。</summary>
    [Theory] [MemberData(nameof(NativeModes))]
    public async Task SameBinding_ReplaysOriginalInput(string mode)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part(mode).GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false); }); var factory = Factory(handler, mode); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, mode, store: store); await Invoke(sdk, mode); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>三个协议分别检查完整目标身份，不能因公开模型同名而迁移。</summary>
    public static IEnumerable<object[]> DriftCases()
    { foreach (var mode in new[] { "openrouter-chat", "xinference-text", "openrouter-responses" }) foreach (var field in new[] { "root", "model", "key", "client", "policy", "version" }) yield return [mode, field]; }
    /// <summary>服务器、实际模型、凭据、命名客户端、策略和版本都在第二次 IO 前受约束。</summary>
    [Theory] [MemberData(nameof(DriftCases))]
    public async Task CompleteTargetDrift_IsRejected(string mode, string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = Factory(handler, mode); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, store: store)) await Invoke(first, mode);
        var target = Target(mode); target = field switch { "root" => target with { BaseAddress = Target(mode, 1).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "changed" }, _ => target };
        await using var second = Build(factory, mode, store: store, target: target, key: field == "key" ? "other-key" : null, client: field == "client" ? "Explicit.Changed" : null, options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>相同公开模型也不能跨原生服务复用完整会话绑定。</summary>
    [Theory] [InlineData("xinference-chat")] [InlineData("submodel-chat")]
    public async Task DifferentNativeService_IsRejected(string mode)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("openrouter-chat").GetProperty("result").GetRawText())), false))); var factory = Factory(handler, "openrouter-chat"); var store = CompactionFixture.Store(); await using (var first = Build(factory, "openrouter-chat", store: store)) await Invoke(first, "openrouter-chat");
        await using var second = Build(factory, mode, store: store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有明确配置的原生 HTTP 错误原文保留，完整失败不触发重试。</summary>
    public static IEnumerable<object[]> Errors() { foreach (var mode in Modes) foreach (var stream in new[] { false, true }) yield return [mode, stream]; }
    /// <summary>原始状态、错误元数据和 UTF-8 正文不转成成功对象。</summary>
    [Theory] [MemberData(nameof(Errors))]
    public async Task NativeHttpError_IsOriginal(string mode, bool stream)
    {
        var source = Window.GetProperty("http_error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: HttpStatusCode.BadRequest))); var factory = Factory(handler, mode); await using var sdk = Build(factory, mode);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, stream, [])); Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
