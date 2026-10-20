using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.XaiFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式原生 xAI Chat/Responses 的完整历史、加密项、工具关联和原始用量。</summary>
public sealed class XaiCompatibleTests
{
    /// <summary>原生和代理根分别覆盖两种协议的完整与流式窗口。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var mode in new[] { "chat", "responses" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整 caller/wire/result/events 核对不允许模型字段、加密内容、关联或精确值丢失。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes); var target = Target(mode, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty("root").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
            var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("xai-transport", seen[i].EventId); Assert.Null(seen[i].EventName); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal("public", seen[i].Metadata.PublicModel); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal("reported-native-grok", result.Data.GetProperty("model").GetString()); Assert.Equal("grok-4.7", result.Metadata.UpstreamModel); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式加密历史、倒序工具结果和原生字段跨实例原样重放，不注入隐藏对话或存储默认值。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")]
    public async Task SameBinding_ReplaysWholeHistory(string mode)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part(mode).GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        for (var i = 0; i < 2; i++) { await using var sdk = Build(factory, mode, store: store); await Invoke(sdk, mode); } Assert.Equal(2, handler.Calls);
    }
    /// <summary>每种协议的完整服务器、模型、Key、实现配置和版本变化均不能迁移已绑定会话。</summary>
    public static IEnumerable<object[]> DriftCases()
    { foreach (var mode in new[] { "chat", "responses" }) foreach (var field in new[] { "root", "model", "key", "client", "policy", "version" }) yield return [mode, field]; }
    /// <summary>同名公开模型和凭据标识不等于同一完整上游目标。</summary>
    [Theory] [MemberData(nameof(DriftCases))]
    public async Task BoundTargetDrift_PrecedesAnotherRequest(string mode, string field)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part(mode).GetProperty("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); await using (var first = Build(factory, mode, store: store)) await Invoke(first, mode);
        var target = Target(mode); target = field switch { "root" => target with { BaseAddress = Target(mode, 1).BaseAddress }, "model" => target with { UpstreamModel = "other" }, "version" => target with { Version = "2" }, _ => target };
        await using var second = Build(factory, mode, store: store, target: target, key: field == "key" ? "other-key" : null, client: field == "client" ? "Explicit.Changed" : "Explicit.XAI.Native", options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>被上游拒绝的显式模型仍不能触发本地后缀改写、参数重命名、搜索注入或再次请求。</summary>
    [Theory] [InlineData("selected-model-search")] [InlineData("grok-3-mini-high")] [InlineData("grok-3-mini-low")]
    public async Task SelectedModel_DoesNotTriggerParameterRepair(string model)
    {
        var request = (ChatCompletionRequest)Request("chat"); request.Parameters.Remove("search_parameters");
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("chat").GetProperty("wire").GetRawText())!.AsObject(); expected["model"] = model; expected.Remove("search_parameters"); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("http_error").GetRawText())), false, status: HttpStatusCode.BadRequest); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "chat", target: Target("chat") with { UpstreamModel = model });
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "chat", request: request)); Assert.Equal(Window.GetProperty("http_error").GetRawText(), failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>两种协议的原生错误 JSON 保留 HTTP 状态与完整错误正文，不隐式修补请求。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task OriginalHttpError_IsPreserved(string mode, bool stream)
    {
        var source = Window.GetProperty("http_error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: HttpStatusCode.BadRequest))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, stream, [])); Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>嵌套推理与供应商旧式替代字段分别原样传递，不在 SDK 中模拟上游优先级。</summary>
    [Theory] [InlineData("nested")] [InlineData("flat")] [InlineData("both")]
    public async Task ReasoningAlternatives_RemainNative(string mode)
    {
        var request = (ResponseRequest)Request("responses"); var parameters = new Dictionary<string, JsonElement>(request.Parameters); if (mode == "flat") parameters.Remove("reasoning"); if (mode != "nested") parameters["reasoning_effort"] = JsonSerializer.SerializeToElement("low"); request = request with { Parameters = parameters };
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("responses").GetProperty("wire").GetRawText())!.AsObject(); if (mode == "flat") expected.Remove("reasoning"); if (mode != "nested") expected["reasoning_effort"] = "low"; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses"); await Invoke(sdk, "responses", request: request); Assert.Equal(1, handler.Calls);
    }
    /// <summary>存储与响应链由调用者显式选择；跨实例续接只发送本次输入，不插入已有历史。</summary>
    [Fact]
    public async Task ExplicitStoredContinuation_DoesNotInjectHistory()
    {
        var index = 0; using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("responses").GetProperty("wire").GetRawText())!.AsObject(); expected["store"] = true; if (index++ != 0) { expected["input"] = "Continue"; expected["previous_response_id"] = "xai-response-A"; } Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("responses").GetProperty("result").GetRawText())), false); });
        var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); var request = (ResponseRequest)Request("responses"); request.Parameters["store"] = JsonSerializer.SerializeToElement(true);
        await using (var first = Build(factory, "responses", store: store)) await Invoke(first, "responses", request: request);
        await using (var second = Build(factory, "responses", store: store)) await Invoke(second, "responses", request: request with { Input = JsonValue.Create("Continue"), PreviousResponseId = "xai-response-A" }); Assert.Equal(2, handler.Calls);
    }
}
