using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>generate 的完整金样通过公共 SDK 验证，不从 Chat 或生产映射生成预期。</summary>
public sealed class NativeGenerateTests
{
    /// <summary>原生 prompt/context/schema/logprobs 和未知字段在完整/流式、匿名/Key 下保持完整。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task GenerateWireAndResults_AreNative(bool stream, bool anonymous)
    {
        var events = JsonSerializer.Deserialize<JsonElement[]>(OllamaFixture.Text("generate-events"))!;
        var raw = stream ? string.Join("\r\n", events.Select(item => item.GetRawText())) : OllamaFixture.Text("generate-response");
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(raw));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("http://native.invalid/prefix/api/generate", message.RequestUri!.AbsoluteUri); Assert.Equal(anonymous ? null : "Bearer fixture-secret", message.Headers.Authorization?.ToString());
            var expected = JsonNode.Parse(OllamaFixture.Text("generate-wire"))!; expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "application/x-ndjson" : "application/json"); return response;
        });
        var factory = new OllamaHttpFactory(handler); await using var client = OllamaFixture.Client(new(factory), anonymous, targets: [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }]);
        if (stream)
        {
            var index = 0; await foreach (var item in client.LLM.OllamaGenerate.StreamAsync(Request(), new() { SessionId = "generate" }, TestContext.Current.CancellationToken))
            {
                Assert.True(JsonElement.DeepEquals(events[index], item.Data)); Assert.Equal(index == events.Length - 1, item.IsTerminal);
                Assert.Equal(item.IsTerminal, body.Eof); Assert.Equal(item.IsTerminal, body.Disposed); Assert.Equal(item.IsTerminal, factory.Clients[0].Disposed);
                Assert.Null(item.EventName); Assert.Null(item.EventId); Assert.Null(item.RetryMilliseconds); Assert.Null(item.Id); index++;
            }
            Assert.Equal(events.Length, index);
        }
        else
        {
            var result = await client.LLM.OllamaGenerate.GenerateAsync(Request(), new() { SessionId = "generate" }, TestContext.Current.CancellationToken);
            Assert.True(OllamaFixture.Same(OllamaFixture.Text("generate-response"), result.Data)); Assert.Equal("{\"x\":0}", result.Response); Assert.True(result.Done); Assert.Null(result.Id);
            Assert.False(result.Data.TryGetProperty("message", out _)); Assert.False(result.Data.TryGetProperty("usage", out _)); Assert.True(body.Disposed); Assert.True(factory.Clients[0].Disposed);
        }
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>prompt 的省略/空串/null 保持原生区别，空请求仍可按上游契约 load/unload。</summary>
    [Theory] [InlineData("omitted")] [InlineData("\"\"")] [InlineData("null")]
    public async Task MinimalRequest_DoesNotInjectPrompt(string prompt)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject(); Assert.Equal(prompt != "omitted", wire.ContainsKey("prompt"));
            if (prompt != "omitted") Assert.True(JsonNode.DeepEquals(JsonNode.Parse(prompt), wire["prompt"]));
            Assert.False(wire.ContainsKey("messages")); return new(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"native\",\"response\":\"\",\"done\":true,\"done_reason\":\"load\"}", Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), targets: [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }]);
        var request = new OllamaGenerateRequest { Model = "public" }; if (prompt != "omitted") request.Parameters["prompt"] = JsonSerializer.Deserialize<JsonElement>(prompt);
        var items = new List<OllamaGenerateEvent>(); await foreach (var item in client.LLM.OllamaGenerate.StreamAsync(request, new() { SessionId = "load" }, TestContext.Current.CancellationToken)) items.Add(item);
        Assert.True(Assert.Single(items).IsTerminal); Assert.Equal("load", items[0].Data.GetProperty("done_reason").GetString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整原生参数在缓存等待前复制，不因调用者随后修改字典而改变请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task GenerateSnapshot_PrecedesSessionAwait(bool stream)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(OllamaFixture.Store()) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = JsonNode.Parse(OllamaFixture.Text("generate-wire"))!; expected["stream"] = stream; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return new(HttpStatusCode.OK) { Content = new StringContent(GenerateTerminalTests.End, Encoding.UTF8, "application/json") };
        });
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), store: store, targets: [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }]); var request = Request();
        async Task Invoke()
        { if (stream) { await foreach (var _ in client.LLM.OllamaGenerate.StreamAsync(request, new() { SessionId = "copy" }, TestContext.Current.CancellationToken)) { } } else await client.LLM.OllamaGenerate.GenerateAsync(request, new() { SessionId = "copy" }, TestContext.Current.CancellationToken); }
        var call = Invoke(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); request.Parameters.Clear(); release.TrySetResult(); await call; Assert.Equal(1, handler.Calls);
    }
    /// <summary>Chat 和 generate 即使同一上游也不共用协议绑定或自动转换。</summary>
    [Fact]
    public async Task GenerateSession_CannotBecomeChat()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GenerateTerminalTests.End, Encoding.UTF8, "application/json") }));
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), targets: [OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate | LlmProtocol.OllamaChat }]);
        await client.LLM.OllamaGenerate.GenerateAsync(Request(), new() { SessionId = "one" }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.OllamaChat.CompleteAsync(OllamaFixture.Request(), new() { SessionId = "one" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 generate 在两个客户端和完整/流式模式间恢复同一完整目标。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task GenerateSession_RestoresWholeTarget(bool firstStream, bool secondStream)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("http://second.invalid/api/generate", message.RequestUri!.AbsoluteUri);
            Assert.Equal("second-model", JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!["model"]!.GetValue<string>()); Assert.Null(message.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent(GenerateTerminalTests.End, Encoding.UTF8, "application/json") };
        });
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler)); var store = OllamaFixture.Store();
        var targets = new[] { OllamaFixture.Target() with { Protocols = LlmProtocol.OllamaGenerate }, OllamaFixture.Target() with { Id = "second", Protocols = LlmProtocol.OllamaGenerate, BaseAddress = new("http://second.invalid/api/"), UpstreamModel = "second-model" } };
        async Task Invoke(bool stream, int selected)
        {
            await using var client = OllamaFixture.Client(provider, store: store, targets: targets, selected: selected);
            if (stream) { await foreach (var _ in client.LLM.OllamaGenerate.StreamAsync(Request(), new() { SessionId = "restore" }, TestContext.Current.CancellationToken)) { } }
            else await client.LLM.OllamaGenerate.GenerateAsync(Request(), new() { SessionId = "restore" }, TestContext.Current.CancellationToken);
        }
        await Invoke(firstStream, 1); await Invoke(secondStream, 0); Assert.Equal(2, handler.Calls);
    }
    /// <summary>原生结构直接装入扩展参数，不将 prompt 转成 messages。</summary>
    internal static OllamaGenerateRequest Request() => JsonSerializer.Deserialize<OllamaGenerateRequest>(OllamaFixture.Text("generate-request"))!;
}
