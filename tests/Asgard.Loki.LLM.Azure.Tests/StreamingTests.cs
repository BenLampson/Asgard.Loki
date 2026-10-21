using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>完整原生 SSE 金样包含异步过滤尾部、未知事件、工具关联和原始传输身份。</summary>
public sealed class StreamingTests
{
    /// <summary>增量不等待 EOF，终态/过滤/用量尾帧在正常释放后按原序交付。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NativeEvents_MatchFullIndependentGoldens(bool responses, bool external)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses))); var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType); var expected = JsonNode.Parse(AzureFixture.Text(responses ? "responses-wire" : "chat-v1-wire"))!.AsObject(); expected["stream"] = true;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            if (external) Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); else Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key")));
            return Reply(stream);
        });
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock); await using var client = AzureFixture.Client(provider, responses, external: external);
        var expectedEvents = JsonSerializer.Deserialize<JsonElement>(AzureFixture.Text(responses ? "responses-events" : "chat-events")); var index = 0;
        await foreach (var item in PublicStream(client, responses))
        {
            Assert.True(JsonElement.DeepEquals(expectedEvents[index], item.Data)); Assert.Equal(responses ? "wire-response" : "wire-1", item.EventId); Assert.Equal(responses ? 25L : 1500L, item.RetryMilliseconds);
            Assert.Equal(responses ? item.Data.GetProperty("type").GetString() : "chat.native", item.EventName);
            var held = responses ? index == expectedEvents.GetArrayLength() - 1 : index >= 3;
            Assert.Equal(held, stream.Eof); Assert.Equal(held, stream.Disposed); Assert.Equal(held, Assert.Single(factory.Clients).Disposed); index++;
        }
        Assert.Equal(expectedEvents.GetArrayLength(), index); Assert.Equal(external ? 1 : 0, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>failed/incomplete/error 保留原生失败终态，不变成 completed。</summary>
    [Theory] [InlineData("failed")] [InlineData("incomplete")] [InlineData("error")]
    public async Task ResponseFailureTerminal_RemainsNative(string status)
    {
        var data = status == "error" ? """{"type":"error","code":"fixture","message":"原生错误"}""" : "{\"type\":\"response." + status + "\",\"response\":{\"id\":\"resp\",\"status\":\"" + status + "\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"error\":{\"code\":\"fixture\"}}}";
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new AzureHttpFactory(handler);
        var events = new List<LlmJsonEvent>(); await foreach (var item in DirectStream(new(factory), true, TestContext.Current.CancellationToken)) { Assert.True(stream.Eof); Assert.True(stream.Disposed); events.Add(item); }
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(data), Assert.Single(events).Data)); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>在有用增量后发生尾部故障时，先前增量可见，但没有任何成功终态。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(false, "dispose")] [InlineData(false, "cancel")] [InlineData(true, "read")] [InlineData(true, "dispose")] [InlineData(true, "cancel")]
    public async Task LateFailure_WithholdsNativeTerminal(bool responses, string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses))) { FailAtEof = failure == "read", FailDispose = failure == "dispose", OnEof = failure == "cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new AzureHttpFactory(handler); var observed = new List<LlmJsonEvent>();
        async Task Consume() { await foreach (var item in DirectStream(new(factory), responses, cancel.Token)) observed.Add(item); }
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume);
        Assert.Equal(responses ? 6 : 3, observed.Count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前退出枚举立即释放本次 HTTP 资源，不重取令牌或重放生成。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ConsumerExit_DisposesOwnedResources(bool responses)
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(AzureFixture.Sse(responses))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new AzureHttpFactory(handler);
        await foreach (var _ in DirectStream(new(factory), responses, TestContext.Current.CancellationToken)) break;
        Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    internal static HttpResponseMessage Reply(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
    internal static async IAsyncEnumerable<LlmJsonEvent> DirectStream(AzureLlmProvider provider, bool responses, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (responses) { await foreach (var item in ((IResponsesAdapter)provider).StreamAsync(AzureFixture.Request(true), AzureFixture.Context(true), token)) yield return item; }
        else { await foreach (var item in provider.StreamAsync(AzureFixture.Request(false), AzureFixture.Context(false), token)) yield return item; }
    }
    private static async IAsyncEnumerable<LlmJsonEvent> PublicStream(Asgard.Loki.Abstractions.ILokiClient client, bool responses)
    {
        var call = new LlmCallOptions { SessionId = "azure-session" }; var token = TestContext.Current.CancellationToken;
        if (responses) { await foreach (var item in client.LLM.Responses.StreamAsync(AzureFixture.Request(true).Deserialize<ResponseRequest>()!, call, token)) yield return item; }
        else { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(AzureFixture.Request(false).Deserialize<ChatCompletionRequest>()!, call, token)) yield return item; }
    }
}
