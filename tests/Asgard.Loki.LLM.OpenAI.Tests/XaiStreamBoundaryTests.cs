using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.XaiFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生 xAI 终态、错误、已知生成与未知业务元数据的分界。</summary>
public sealed class XaiStreamBoundaryTests
{
    /// <summary>官方原生图像工具生成事件同样不能在 Responses 终态后重开，不能按未知元数据放行。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task KnownNativeGeneration_AfterTerminalIsRejected(int index)
    {
        var source = Part("responses").GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", XaiFixture.Frame(Window.GetProperty("post_terminal_generation")[index])); var body = new OllamaBodyStream(Bytes(source)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses"); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, "responses", true, seen)); Assert.DoesNotContain(seen, Ending); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同名前缀的未知业务对象仍完整保留，不能用前缀推测为原生生命周期。</summary>
    [Fact]
    public async Task UnknownSimilarEvent_RemainsOriginal()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"response.image_generation_call.business\",\"id\":\"ordinary\",\"response_id\":\"ordinary-response\",\"error\":{\"business\":false}}"); var source = Part("responses").GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", XaiFixture.Frame(metadata)+Frame("[DONE]")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses"); List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.True(JsonElement.DeepEquals(metadata, seen[^1].Data));
    }
    /// <summary>第一原生 failed 保留完整失败快照；不补造 completed 或抹除原生错误。</summary>
    [Fact]
    public async Task NativeFailed_IsOriginalEvent()
    {
        var expected = Window.GetProperty("failed"); var body = new OllamaBodyStream(Bytes(XaiFixture.Frame(expected))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses"); List<LlmJsonEvent> seen = [];
        await Invoke(sdk, "responses", true, seen, item => { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.True(JsonElement.DeepEquals(expected, Assert.Single(seen).Data)); Assert.Equal("failed", seen[0].Data.GetProperty("response").GetProperty("status").GetString());
    }
    /// <summary>原生不完整终态保留状态与原因，不能修补为正常完成。</summary>
    [Fact]
    public async Task NativeIncomplete_IsNotRewritten()
    {
        var response = JsonNode.Parse(Part("responses").GetProperty("result").GetRawText())!.AsObject(); response["status"] = "incomplete"; response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" }; var expected = JsonSerializer.SerializeToElement(new { type = "response.incomplete", response }); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(XaiFixture.Frame(expected))), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, "responses"); List<LlmJsonEvent> seen = []; await Invoke(sdk, "responses", true, seen); Assert.True(JsonElement.DeepEquals(expected, Assert.Single(seen).Data));
    }
    /// <summary>原生 error 在实际 EOF 与关闭后作为原始协议事件返回，不被当成成功终态。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")]
    public async Task FirstNativeError_IsOriginalEvent(string mode)
    {
        var expected = Window.GetProperty("error"); var body = new OllamaBodyStream(Bytes(XaiFixture.Frame(expected))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        await Invoke(sdk, mode, true, seen, item => { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }); Assert.True(JsonElement.DeepEquals(expected, Assert.Single(seen).Data)); Assert.DoesNotContain(seen, item => item.Type == "response.completed");
    }
    /// <summary>晚期 error 或 failed 撤回已暂存终态，完整原始错误正文仍可检查。</summary>
    [Theory] [InlineData("chat", false)] [InlineData("responses", false)] [InlineData("responses", true)]
    public async Task LateError_DiscardsEnding(string mode, bool failed)
    {
        var error = Window.GetProperty(failed ? "failed" : "error"); var source = Part(mode).GetProperty("sse").GetString()!.Replace("data: [DONE]\n\n", XaiFixture.Frame(error)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Invoke(sdk, mode, true, seen)); Assert.Equal(JsonSerializer.Serialize(error), failure.ResponseBody); Assert.DoesNotContain(seen, Ending); Assert.Equal(1, handler.Calls);
    }
    /// <summary>Chat 实际 DONE 不能缺失或重复，已知 Responses 终态不能重复、缺少身份或重开内容。</summary>
    [Theory] [InlineData("chat", "missing")] [InlineData("chat", "duplicate")] [InlineData("chat", "after-done")] [InlineData("responses", "missing")] [InlineData("responses", "duplicate")] [InlineData("responses", "identity")] [InlineData("responses", "reasoning")] [InlineData("responses", "function")]
    public async Task InvalidNativeEnding_IsRejected(string mode, string fault)
    {
        var source = Part(mode).GetProperty("sse").GetString()!;
        if (fault == "missing") source = mode == "chat" ? source.Replace("data: [DONE]\n\n", "") : string.Concat(Part(mode).GetProperty("events").EnumerateArray().Take(14).Select(XaiFixture.Frame));
        else if (fault == "duplicate") source = mode == "chat" ? source+Frame("[DONE]") : source.Replace("data: [DONE]\n\n", XaiFixture.Frame(Part(mode).GetProperty("events")[14]));
        else if (fault == "after-done") source += Frame("{\"type\":\"vendor.trace\"}");
        else if (fault == "identity") source = source.Replace("\"id\":\"xai-response-A\",\"object\"", "\"id\":\"other-native\",\"object\"");
        else source = source.Replace("data: [DONE]\n\n", Frame(fault == "reasoning" ? "{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"late\"}" : "{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"late\"}"));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(source)), true))); var factory = new OpenAiHttpFactory(handler); await using var sdk = Build(factory, mode); List<LlmJsonEvent> seen = []; await Assert.ThrowsAsync<InvalidDataException>(() => Invoke(sdk, mode, true, seen)); Assert.DoesNotContain(seen, Ending);
    }
}
