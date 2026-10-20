using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.ZhipuV4Fixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式原生 智谱 v4 Chat/Responses 的完整历史、加密项、工具关联和原始用量。</summary>
public sealed class ZhipuV4CompatibleTests
{
    /// <summary>原生和代理根分别覆盖两种协议的完整与流式窗口。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var profile in new[] { 0, 1 }) foreach (var mode in new[] { "chat", "responses" }) foreach (var stream in new[] { false, true }) yield return [profile, mode, stream]; }
    /// <summary>完整 caller/wire/result/events 核对不允许模型字段、原生内容、关联或精确值丢失。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WholeNativeWindows_ArePreserved(int profile, string mode, bool stream)
    {
        var part = Part(mode); var source = stream ? part.GetProperty("sse").GetString()! : part.GetProperty("result").GetRawText(); var bytes = Bytes(source); var body = new OllamaBodyStream(bytes); var target = Target(mode, profile); if (profile == 1) target = target with { BaseAddress = new(target.BaseAddress.AbsoluteUri.TrimEnd('/')) };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Profile(profile).GetProperty(mode+"_root").GetString()+part.GetProperty("path").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(Profile(profile).GetProperty("key").GetString(), message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(part.GetProperty(stream ? "wire_stream" : "wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream, bytes.Length);
        }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode, profile, target: target);
        if (stream)
        {
            List<LlmJsonEvent> seen = []; await Invoke(sdk, mode, true, seen, item => { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } });
            var expected = part.GetProperty("events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("z-native-transport", seen[i].EventId); Assert.Null(seen[i].EventName); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal("public", seen[i].Metadata.PublicModel); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else { var result = await Invoke(sdk, mode); Assert.NotNull(result); Assert.True(JsonElement.DeepEquals(part.GetProperty("result"), result.Data)); Assert.Equal("reported-native-z-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式原生历史、倒序工具结果和原生字段跨实例原样重放，不注入隐藏对话或存储默认值。</summary>
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
        await using var second = Build(factory, mode, store: store, target: target, key: field == "key" ? "other-key" : null, client: field == "client" ? "Explicit.Changed" : "Explicit.ZhipuV4.Native", options: field == "policy" ? new() { MaximumBodyBytes = 8192 } : null); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, mode)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生错误保留状态与原文，两个协议均无 SDK 重试。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task NativeHttpError_IsOriginal(string mode, bool stream)
    {
        var source = Window.GetProperty("http_error").GetRawText(); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false, status: HttpStatusCode.BadRequest))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, stream, [])); Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缺少真实原生终态时不能将完整文本、finish 或 EOF 当作成功。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")]
    public async Task MissingNativeTerminal_DiscardsEnding(string mode)
    {
        var events = Part(mode).GetProperty("events").EnumerateArray().Where(e => mode == "chat" || e.GetProperty("type").GetString() != "response.completed"); var source = string.Concat(events.Select(ZhipuV4Fixture.Frame));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>真实终态后不能继续生成已知内容，也不能重复终态。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task PostTerminalContent_IsRejected(string mode, bool duplicate)
    {
        var after = mode == "chat" ? duplicate ? "[DONE]" : "{\"id\":\"z-chat-A\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"late\"}}]}" : duplicate ? Part(mode).GetProperty("events")[14].GetRawText() : "{\"type\":\"response.output_text.delta\",\"response_id\":\"z-response-A\",\"delta\":\"late\"}";
        var source = Part(mode).GetProperty("sse").GetString()!+OpenAiHttpFixture.Frame(mode == "chat" && duplicate ? "[DONE]" : JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(after)));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
    /// <summary>failed JSON 按既有原文异常契约返回；原生 SSE 与 incomplete 保留状态和部分输出。</summary>
    [Theory] [InlineData("failed", false)] [InlineData("failed", true)] [InlineData("incomplete", false)] [InlineData("incomplete", true)]
    public async Task NativeUnsuccessfulOutcome_IsOriginal(string status, bool stream)
    {
        var result = JsonNode.Parse(Part("responses").GetProperty("result").GetRawText())!; result["status"] = status;
        if (status == "failed") result["error"] = new JsonObject { ["code"] = "content_filter", ["message"] = "原生过滤" }; else result["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
        var expected = JsonSerializer.SerializeToElement(result); var terminal = JsonSerializer.SerializeToElement(new { type = "response."+status, sequence_number = 1, response = result });
        var source = stream ? ZhipuV4Fixture.Frame(Part("responses").GetProperty("events")[0])+ZhipuV4Fixture.Frame(terminal) : result.ToJsonString(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), stream))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses");
        if (stream) { List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.Equal(2, seen.Count); Assert.True(JsonElement.DeepEquals(terminal, seen[1].Data)); } else if (status == "failed") { var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "responses")); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(source, failure.ResponseBody); } else { var actual = await Invoke(sdk, "responses"); Assert.NotNull(actual); Assert.True(JsonElement.DeepEquals(expected, actual.Data)); } Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型拒绝的原始参数及 data URI 不由 SDK 修正或剥离。</summary>
    [Fact]
    public async Task RejectedNativeInput_IsNotRepaired()
    {
        var request = (ChatCompletionRequest)Request("chat"); request.Parameters["max_completion_tokens"] = JsonSerializer.SerializeToElement(256); request.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAEC", ["detail"] = "high" } }) });
        using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("chat").GetProperty("wire").GetRawText())!.AsObject(); expected["messages"] = request.Messages.DeepClone(); expected["max_completion_tokens"] = 256; Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Window.GetProperty("http_error").GetRawText())), false, status: HttpStatusCode.BadRequest); }); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "chat"); await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, "chat", request: request)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实 EOF 读取失败或释放时取消均撤回缓冲终态。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("chat", true)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task LateFailure_DiscardsEnding(string mode, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Bytes(Part(mode).GetProperty("sse").GetString()!)) { FailAtEof = !cancel }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (cancel) cancellation.Cancel(); } }; await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Invoke(sdk, mode, true, seen, cancellationToken: cancellation.Token)); Assert.NotNull(failure); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
