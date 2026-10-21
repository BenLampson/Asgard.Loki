using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.SiliconFlowFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式原生 Chat、推理、工具、结构化、多模态、Prefix/FIM 保留完整调用窗口。</summary>
public sealed class SiliconFlowCompatibleTests
{
    /// <summary>原生与代理完整根分别核对七种独立原生模式的 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var mode in new[] { "reasoning", "tools", "schema", "json", "media", "prefix", "fim" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整 wire/result/events 逐字段一致，不把字段传递当成真实模型效果证明。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes); var target = Target(mode, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+Window.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<ChatCompletionEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("sf-transport", seen[i].EventId); Assert.Equal(1500L, seen[i].RetryMilliseconds); Assert.Null(seen[i].EventName); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal("reported-native-sf-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>空消息、空前后缀由调用者负责，SDK 不用虚构 user 消息掩盖上游拒绝。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RejectedFim_DoesNotFabricateInput(bool emptyPrefix)
    {
        var request = Request("fim") with { Messages = new JsonArray() }; if (emptyPrefix) { request.Parameters["prefix"] = JsonSerializer.SerializeToElement(""); request.Parameters["suffix"] = JsonSerializer.SerializeToElement(""); }
        var error = Window.GetProperty("error").GetRawText(); using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("fim").GetProperty("wire").GetRawText())!.AsObject(); expected["messages"] = new JsonArray(); if (emptyPrefix) { expected["prefix"] = ""; expected["suffix"] = ""; } Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(error)), false, status: HttpStatusCode.BadRequest); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "fim");
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "fim", request: request)); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>调用者显式开关与预算保持字面值，不按模型名或文档歧义反转布尔值或修补 effort。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ThinkingControls_AreNotRepaired(bool thinking)
    {
        var request = Request("reasoning"); request.Parameters["enable_thinking"] = JsonSerializer.SerializeToElement(thinking); request.Parameters["reasoning_effort"] = JsonSerializer.SerializeToElement("low");
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("reasoning").GetProperty("wire").GetRawText())!.AsObject(); expected["enable_thinking"] = thinking; expected["reasoning_effort"] = "low"; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("error").GetRawText())), false, status: HttpStatusCode.BadRequest); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "reasoning"); await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "reasoning", request: request)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 HTTP 数字 code/message/data 错误保持原文，不被当成 OpenAI error 对象或自动重试。</summary>
    [Theory] [InlineData(false, 400)] [InlineData(true, 400)] [InlineData(false, 429)] [InlineData(true, 429)] [InlineData(false, 503)] [InlineData(true, 503)]
    public async Task NativeHttpError_IsOriginal(bool stream, int status)
    {
        var source = Window.GetProperty(status == 503 ? "overloaded" : "error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: (HttpStatusCode)status))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "tools");
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "tools", stream, [])); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型绑定与协议原生历史跨实例恢复；FIM 仍由调用者明确提供前后缀和消息。</summary>
    [Theory] [InlineData("tools")] [InlineData("fim")]
    public async Task SameBinding_ReplaysOriginalInput(string mode)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part(mode).GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, mode, store: store); await Invoke(sdk, mode); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>同一公开模型、完整服务器及实际输入模式仍受完整 Session 配置绑定。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("client")] [InlineData("policy")] [InlineData("version")]
    public async Task CompleteTargetDrift_IsRejected(string field)
    {
        const string mode = "fim"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, store: store)) await Invoke(first, mode);
        var target = Target(mode); target = field switch { "root" => target with { BaseAddress = Target(mode, 1).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(factory, mode, store: store, target: target, key: field == "key" ? "other-key" : null, client: field == "client" ? "Explicit.Changed" : "Explicit.SiliconFlow.Native", options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
}
