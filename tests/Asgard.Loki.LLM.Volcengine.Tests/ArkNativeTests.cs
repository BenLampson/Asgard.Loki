using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>整段原生字段和完整流逐帧与独立窗口比较，不投影工具、推理或实际返回模型。</summary>
public sealed class ArkNativeTests
{
    /// <summary>JSON/SSE 保持完整 wire/result/transport 身份，原生结束帧在 EOF 和所有拥有资源关闭后可见。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task FullNativeWindows_ArePreserved(bool responses, bool stream)
    {
        var body = new OllamaBodyStream(Bytes(stream ? Text(responses ? "responses.sse" : "chat.sse") : Part(responses ? "responses_result" : "chat_result").GetRawText()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Part(responses ? "responses_endpoint" : "chat_endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Key, message.Headers.Authorization!.Parameter); Assert.Equal(message.RequestUri.Authority, message.Headers.Host);
            var expected = JsonNode.Parse(Part(responses ? "responses_wire" : "chat_wire").GetRawText())!.AsObject(); expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(body, stream);
        }); using var factory = new ArkHttpFactory(handler) { Configure = c => { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "other-default"); c.DefaultRequestHeaders.Host = "other.invalid"; } }; var provider = new VolcengineLlmProvider(factory);
        if (stream)
        {
            var events = await Stream(provider, responses, TestContext.Current.CancellationToken, item => { if (Ending(item.Data)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } }); var expected = Frames(responses); Assert.Equal(expected.Count, events.Count);
            for (var i = 0; i < events.Count; i++) { Assert.Equal(expected[i], events[i].Data.GetRawText()); Assert.Equal("transport-ark", events[i].EventId); Assert.Equal(0L, events[i].RetryMilliseconds); Assert.Equal(i == 0 ? responses ? "response.created" : "native.chat" : null, events[i].EventName); Assert.Equal("configured-model", events[i].Metadata.UpstreamModel); }
            if (responses) Assert.Equal("length", events[6].Data.GetProperty("response").GetProperty("incomplete_details").GetProperty("reason").GetString());
            else { Assert.Equal("call-b", events[0].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("id").GetString()); Assert.Equal(0, events[1].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("index").GetInt32()); }
        }
        else
        { var payload = Payload(responses); var before = payload.ToJsonString(); var data = await Complete(provider, responses, TestContext.Current.CancellationToken, payload); Assert.Equal(Part(responses ? "responses_result" : "chat_result").GetRawText(), data.GetRawText()); Assert.Equal("reported-model", data.GetProperty("model").GetString()); Assert.Equal(before, payload.ToJsonString()); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>直接适配器也在工厂回调之前冻结原生 DOM，不能受调用者修改影响。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Snapshot_PrecedesFactory(bool responses)
    {
        var payload = Payload(responses); using var handler = new StubHttpHandler(async (message, token) => { Assert.True(JsonElement.DeepEquals(Part(responses ? "responses_wire" : "chat_wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part(responses ? "responses_result" : "chat_result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler) { Configure = _ => payload.Clear() }; await Complete(new(factory), responses, TestContext.Current.CancellationToken, payload); Assert.Empty(payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>模型形似 Bot/思考后缀也不改路由或名称，不补默认 thinking、tools 或其他参数。</summary>
    [Theory] [InlineData("bot-explicit-selector")] [InlineData("deepseek-explicit-thinking")]
    public async Task ExplicitModel_DoesNotInferEndpointOrRewriteSettings(string model)
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Part("chat_endpoint").GetString(), message.RequestUri!.OriginalString); var data = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(model, data.GetProperty("model").GetString()); Assert.False(data.TryGetProperty("thinking", out _)); Assert.False(data.TryGetProperty("tools", out _)); return Reply(new OllamaBodyStream(Bytes(Part("chat_result").GetRawText())), false); });
        using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory); await Complete(provider, false, TestContext.Current.CancellationToken, new(), Context(provider, Target() with { UpstreamModel = model })); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP、HTTP200 原生错误与二进制错误保留原始字节/媒体，只执行一次请求。</summary>
    [Theory] [InlineData(400, false)] [InlineData(429, false)] [InlineData(200, false)] [InlineData(503, true)]
    public async Task RawErrors_ArePreservedWithoutRetry(int status, bool binary)
    {
        var bytes = binary ? new byte[] { 0xff, 0x80, 0 } : Bytes(Part("error").GetRawText()); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var result = Reply(body, false, status: status); result.Content.Headers.ContentType = new(binary ? "application/octet-stream" : "application/problem+json"); return Task.FromResult(result); }); using var factory = new ArkHttpFactory(handler);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => Complete(new(factory), true, TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(status, (int)error.StatusCode); Assert.Equal(binary ? "application/octet-stream" : "application/problem+json", error.ContentType); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
