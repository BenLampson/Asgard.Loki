using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.TextCompletionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生文本补全的完整调用窗口，不转为 Chat 或重写 prompt、FIM、选择索引与 logprobs。</summary>
public sealed class TextCompletionNativeTests
{
    /// <summary>直接适配器和公共 SDK 均逐字段保留窗口；结束阶段等待 EOF 和所有拥有资源关闭。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task FullNativeWindows_ArePreserved(bool sdk, bool stream)
    {
        var source = stream ? Text("events.sse") : Part("result").GetRawText(); var body = new OllamaBodyStream(Bytes(source));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(Part("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(message.RequestUri.Authority, message.Headers.Host); Assert.Equal(Key, message.Headers.Authorization!.Parameter); Assert.Equal("Bearer", message.Headers.Authorization.Scheme);
            var expected = JsonNode.Parse(Part("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return TextCompletionFixture.Reply(body, stream, Bytes(source).Length);
        }); var factory = new OpenAiHttpFactory(handler) { Configure = c => { c.DefaultRequestHeaders.Authorization = new("Bearer", "other-default"); c.DefaultRequestHeaders.Host = "other.invalid"; } }; var provider = new OpenAiLlmProvider(factory); var payload = Payload(); var original = payload.ToJsonString(); List<TextCompletionEvent> seen = []; JsonElement data = default;
        void Observe(TextCompletionEvent item) { if (Ending(item)) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); } seen.Add(item); }
        if (sdk)
        {
            await using var client = Build(factory); var request = Request(); var before = JsonSerializer.Serialize(request);
            if (stream) await foreach (var item in client.LLM.TextCompletions.StreamAsync(request, new() { SessionId = "text-session" }, TestContext.Current.CancellationToken)) Observe(item);
            else data = (await client.LLM.TextCompletions.CompleteAsync(request, new() { SessionId = "text-session" }, TestContext.Current.CancellationToken)).Data;
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        else if (stream) await TextCompletionFixture.Stream(provider, TestContext.Current.CancellationToken, payload, Observe); else data = await TextCompletionFixture.Complete(provider, TestContext.Current.CancellationToken, payload);
        if (stream)
        {
            var expected = Events(); Assert.Equal(expected.Length, seen.Count);
            for (var i = 0; i < seen.Count; i++) { Assert.True(JsonElement.DeepEquals(expected[i], seen[i].Data)); Assert.Equal("transport-text", seen[i].EventId); Assert.Equal(0L, seen[i].RetryMilliseconds); Assert.Equal(i == 0 ? "native.text" : null, seen[i].EventName); Assert.Equal(Target().UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion); }
        }
        else Assert.True(JsonElement.DeepEquals(Part("result"), data));
        Assert.Equal(original, payload.ToJsonString()); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>首次工厂回调前冻结原始 DOM，调用方清空不影响实际请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeSnapshot_PrecedesFactory(bool stream)
    {
        var payload = Payload(); using var handler = new StubHttpHandler(async (message, token) => { var expected = JsonNode.Parse(Part("wire").GetRawText())!.AsObject(); expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return TextCompletionFixture.Reply(new OllamaBodyStream(Bytes(stream ? Text("events.sse") : Part("result").GetRawText())), stream); }); var factory = new OpenAiHttpFactory(handler) { Configure = _ => payload.Clear() }; var provider = new OpenAiLlmProvider(factory);
        if (stream) await TextCompletionFixture.Stream(provider, TestContext.Current.CancellationToken, payload); else await TextCompletionFixture.Complete(provider, TestContext.Current.CancellationToken, payload); Assert.Empty(payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生字符串、数组和显式省略只验证传输保真，不补 prompt/默认参数或声称远端支持每一种形状。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task NativePayload_IsNotRepaired(int index)
    {
        var payload = JsonNode.Parse(Part("variants")[index].GetRawText())!.AsObject(); using var handler = new StubHttpHandler(async (message, token) => { var expected = payload.DeepClone().AsObject(); expected["model"] = Target().UpstreamModel; expected["stream"] = false; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token)))); return TextCompletionFixture.Reply(new OllamaBodyStream(Bytes(Part("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); await TextCompletionFixture.Complete(new(factory), TestContext.Current.CancellationToken, payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始 HTTP 与 HTTP200 业务错误保留完整字节、媒体和状态，二进制不经 UTF-8 解码。</summary>
    [Theory] [InlineData(400, false)] [InlineData(429, false)] [InlineData(200, false)] [InlineData(503, true)]
    public async Task NativeErrors_AreOriginal(int status, bool binary)
    {
        var bytes = binary ? new byte[] { 0xff, 0x80, 0 } : Bytes(Part("error").GetRawText()); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = TextCompletionFixture.Reply(body, false, status: status); response.Content.Headers.ContentType = new(binary ? "application/octet-stream" : "application/problem+json"); return Task.FromResult(response); }); var factory = new OpenAiHttpFactory(handler);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => TextCompletionFixture.Complete(new(factory), TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(status, (int)error.StatusCode); Assert.Equal(binary ? "application/octet-stream" : "application/problem+json", error.ContentType); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未知的新结束原因保持原文，不规范化成 stop 或推断输出质量。</summary>
    [Fact]
    public async Task FutureFinishReason_IsNotNormalized()
    {
        var raw = JsonNode.Parse(Part("result").GetRawText())!.AsObject(); raw["choices"]![0]!["finish_reason"] = "future-native-stop"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(TextCompletionFixture.Reply(new OllamaBodyStream(Bytes(raw.ToJsonString())), false))); var data = await TextCompletionFixture.Complete(new(new OpenAiHttpFactory(handler)), TestContext.Current.CancellationToken); Assert.Equal("future-native-stop", data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
}
