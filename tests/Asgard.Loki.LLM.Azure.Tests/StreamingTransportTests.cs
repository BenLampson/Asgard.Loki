using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>原生 SSE 的严格传输和外部认证沿用完整目标绑定，不隐式重放。</summary>
public sealed class StreamingTransportTests
{
    /// <summary>两种原生协议均在发送流式请求时使用显式日期路由。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DatedStreaming_UsesExplicitNativeRouting(bool responses)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses)));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://primary.invalid/proxy/openai/" + (responses ? "responses" : "deployments/deploy.1/chat/completions") + "?api-version=explicit-version", request.RequestUri!.AbsoluteUri);
            var expected = JsonNode.Parse(AzureFixture.Text(responses ? "responses-wire" : "chat-dated-wire"))!.AsObject(); expected["stream"] = true;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamingTests.Reply(stream);
        });
        var provider = new AzureLlmProvider(new AzureHttpFactory(handler), new() { ApiStyle = AzureLlmApiStyle.Dated, ApiVersion = "explicit-version" }); var context = AzureFixture.Context(responses, AzureLlmApiStyle.Dated); var count = 0;
        if (responses) { await foreach (var _ in ((IResponsesAdapter)provider).StreamAsync(AzureFixture.Request(true), context, TestContext.Current.CancellationToken)) count++; }
        else { await foreach (var _ in provider.StreamAsync(AzureFixture.Request(false), context, TestContext.Current.CancellationToken)) count++; }
        Assert.Equal(responses ? 7 : 6, count); Assert.True(stream.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>流式创建后的原生响应归属可跨客户端恢复，刷新令牌不换目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamingSession_RestoresTargetWithRefreshedToken(bool responses)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock); var store = AzureFixture.WebSocketStore(); var bodies = new List<OllamaBodyStream>();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString());
            if (responses && source.Calls == 2) Assert.Equal("resp-stream", JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["previous_response_id"]!.GetValue<string>());
            var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses))); bodies.Add(body); return StreamingTests.Reply(body);
        });
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock); var targets = new[] { AzureFixture.Target(responses, AzureLlmApiStyle.V1), AzureFixture.Target(responses, AzureLlmApiStyle.V1, "second") };
        for (var index = 0; index < 2; index++)
        {
            await using var client = AzureFixture.Client(provider, responses, external: true, store: store, targets: targets, selected: index == 0 ? 1 : 0); var request = AzureFixture.Request(responses); var call = new LlmCallOptions { SessionId = "azure-session" };
            if (responses) { if (index == 1) request["previous_response_id"] = "resp-stream"; await foreach (var _ in client.LLM.Responses.StreamAsync(request.Deserialize<ResponseRequest>()!, call, TestContext.Current.CancellationToken)) { } }
            else { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(request.Deserialize<ChatCompletionRequest>()!, call, TestContext.Current.CancellationToken)) { } }
        }
        Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(bodies, body => Assert.True(body.Disposed)); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }
    /// <summary>Content-Length 不匹配时无论正文看似完成也不得释放暂存终态。</summary>
    [Theory] [InlineData(false, -1)] [InlineData(false, 1)] [InlineData(true, -1)] [InlineData(true, 1)]
    public async Task LengthMismatch_WithholdsTerminal(bool responses, int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureFixture.Sse(responses)); var stream = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => { var reply = StreamingTests.Reply(stream); reply.Content.Headers.ContentLength = bytes.Length + difference; return Task.FromResult(reply); }); var factory = new AzureHttpFactory(handler); var observed = new List<LlmJsonEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in StreamingTests.DirectStream(new(factory), responses, TestContext.Current.CancellationToken)) observed.Add(item); });
        Assert.Equal(responses ? 6 : 3, observed.Count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>编码错误与重复属性必须在严格 SSE 解码/JSON 解析处失败。</summary>
    [Theory] [InlineData("encoding")] [InlineData("escape")] [InlineData("duplicate")]
    public async Task InvalidSseEncoding_DoesNotRepairNativeData(string failure)
    {
        var bytes = failure == "encoding" ? Encoding.UTF8.GetBytes("data: {\"future\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}\n\n")).ToArray() : Encoding.UTF8.GetBytes(failure == "escape" ? "data: {\"future\":\"\\ud800\",\"choices\":[]}\n\n" : "data: {\"choices\":[],\"future\":1,\"future\":2}\n\n");
        var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler);
        async Task Consume() { await foreach (var _ in StreamingTests.DirectStream(new(factory), false, TestContext.Current.CancellationToken)) { } }
        if (failure == "encoding") await Assert.ThrowsAsync<DecoderFallbackException>(Consume); else if (failure == "duplicate") await Assert.ThrowsAnyAsync<JsonException>(Consume); else await Assert.ThrowsAsync<InvalidDataException>(Consume);
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>客户端/正文释放时失败或取消都在原生终态公开之前生效。</summary>
    [Theory] [InlineData(false, "client-failure")] [InlineData(true, "client-failure")] [InlineData(false, "client-cancel")] [InlineData(true, "client-cancel")] [InlineData(false, "body-cancel")] [InlineData(true, "body-cancel")]
    public async Task DisposalBoundary_WithholdsTerminal(bool responses, string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses))) { OnDispose = failure == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new AzureHttpFactory(handler) { OnDispose = () => { if (failure == "client-failure") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var count = 0;
        async Task Consume() { await foreach (var _ in StreamingTests.DirectStream(new(factory), responses, cancel.Token)) count++; }
        if (failure == "client-failure") await Assert.ThrowsAsync<IOException>(Consume); else await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume);
        Assert.Equal(responses ? 6 : 3, count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>流式错误保留状态和原文，不退化成另一种成功协议或重新取令牌。</summary>
    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(200)]
    public async Task JsonErrorInsteadOfStream_PreservesNativeFailure(int status)
    {
        const string body = "{\"error\":{\"code\":\"fixture\",\"message\":\"原生错误\"}}"; var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") })); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in provider.StreamAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: true), TestContext.Current.CancellationToken)) { } });
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
