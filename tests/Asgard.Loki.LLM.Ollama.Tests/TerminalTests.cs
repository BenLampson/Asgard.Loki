using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>原生终态、资源释放与失败边界；不将 EOF 或残缺生成伪装成成功。</summary>
public sealed class TerminalTests
{
    /// <summary>缺失终态、改变身份、重复/迟到记录、JSON/UTF-8 错误必须在成功事件之前失败。</summary>
    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("duplicate")] [InlineData("late-partial")] [InlineData("late-error")]
    [InlineData("bad-done")] [InlineData("null-done")] [InlineData("no-model")] [InlineData("no-message")] [InlineData("model-change")]
    [InlineData("bad-message")] [InlineData("bad-reason")] [InlineData("duplicate-json")] [InlineData("duplicate-nested")]
    [InlineData("malformed")] [InlineData("joined")] [InlineData("array")] [InlineData("unknown-only")] [InlineData("bad-utf8")]
    [InlineData("escaped-surrogate")] [InlineData("surrogate-property")]
    public async Task InvalidStream_NeverDeliversTerminal(string kind)
    {
        var end = OllamaFixture.Terminal; var partial = OllamaFixture.Partial;
        var text = kind switch
        {
            "empty" => " \r\n\t", "missing" => partial, "duplicate" => end + "\n" + end,
            "late-partial" => end + "\n" + partial, "late-error" => end + "\n{\"error\":\"late\"}",
            "bad-done" => end.Replace("true", "1"), "null-done" => end.Replace("true", "null"),
            "no-model" => "{\"message\":{},\"done\":true}", "no-message" => "{\"model\":\"native\",\"done\":true}",
            "model-change" => partial + "\n" + end.Replace("native", "other"),
            "bad-message" => end.Replace("\"content\":\"\"", "\"content\":null"), "bad-reason" => end[..^1] + ",\"done_reason\":null}",
            "duplicate-json" => end[..^1] + ",\"done\":true}", "duplicate-nested" => end[..^1] + ",\"future\":{\"x\":1,\"x\":2}}",
            "malformed" => end + "\n{", "joined" => end + end, "array" => "[]", "unknown-only" => "{\"type\":\"future\"}",
            "escaped-surrogate" => end[..^1] + ",\"future\":\"\\ud800\"}", "surrogate-property" => end[..^1] + ",\"\\ud800\":null}", _ => end
        };
        var bytes = kind == "bad-utf8" ? Encoding.UTF8.GetBytes(end[..^1] + ",\"future\":\"").Concat(new byte[] { 0xff, 34, 125 }).ToArray() : Encoding.UTF8.GetBytes(text);
        var body = new OllamaBodyStream(bytes); using var handler = Handler(body); var factory = new OllamaHttpFactory(handler); var provider = new OllamaLlmProvider(factory);
        var items = new List<OllamaChatEvent>(); var error = await Record.ExceptionAsync(async () =>
        { await foreach (var item in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) items.Add(item); });
        Assert.NotNull(error); Assert.True(error is InvalidDataException or JsonException, error.ToString()); Assert.DoesNotContain(items, item => item.IsTerminal);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>取消和晚到读取/释放故障会撤销尚未交付的终态，完整 JSON 同样遵守。</summary>
    [Theory]
    [InlineData(true, "read")] [InlineData(true, "dispose")] [InlineData(true, "eof-cancel")] [InlineData(true, "dispose-cancel")] [InlineData(true, "client-cancel")] [InlineData(true, "client-fail")]
    [InlineData(false, "read")] [InlineData(false, "dispose")] [InlineData(false, "eof-cancel")] [InlineData(false, "dispose-cancel")] [InlineData(false, "client-cancel")] [InlineData(false, "client-fail")]
    public async Task LateFailure_PreventsSuccessfulResult(bool stream, string failure)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(OllamaFixture.Terminal))
        { FailAtEof = failure == "read", FailDispose = failure == "dispose", OnEof = failure == "eof-cancel" ? cancellation.Cancel : null, OnDispose = failure == "dispose-cancel" ? cancellation.Cancel : null };
        using var handler = Handler(body, stream ? "application/x-ndjson" : "application/json");
        var factory = new OllamaHttpFactory(handler) { OnClientDispose = failure == "client-cancel" ? cancellation.Cancel : failure == "client-fail" ? () => throw new IOException("client dispose") : null };
        var provider = new OllamaLlmProvider(factory); var items = new List<OllamaChatEvent>();
        var error = await Record.ExceptionAsync(async () =>
        {
            if (stream) { await foreach (var item in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), cancellation.Token)) items.Add(item); }
            else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), cancellation.Token);
        });
        Assert.NotNull(error); Assert.Empty(items); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
    }
    /// <summary>错误为原生终态，HTTP 200 中的 error 不能误判为成功或制造 done。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeError_PreservesOriginalFailure(bool stream)
    {
        const string raw = "{\"error\":\"model failed\",\"future\":{\"null\":null}}";
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(stream ? OllamaFixture.Partial + "\n" + raw : raw)); using var handler = Handler(body, stream ? "application/x-ndjson" : "application/json");
        var factory = new OllamaHttpFactory(handler); var provider = new OllamaLlmProvider(factory);
        if (stream)
        {
            var items = new List<OllamaChatEvent>(); await foreach (var item in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken))
            { if (item.IsTerminal) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(factory.Clients[0].Disposed); } items.Add(item); }
            Assert.Equal(2, items.Count); var last = items[^1]; Assert.True(last.IsError); Assert.True(last.IsTerminal); Assert.Null(last.Done); Assert.True(OllamaFixture.Same(raw, last.Data));
        }
        else
        {
            var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(raw, error.ResponseBody);
        }
        Assert.Equal(1, handler.Calls); Assert.True(body.Disposed);
    }
    /// <summary>HTTP 失败保留真实状态和原始错误正文，无协议回退和自动重试。</summary>
    [Theory] [InlineData(400, false)] [InlineData(429, false)] [InlineData(503, false)] [InlineData(401, true)] [InlineData(500, true)]
    public async Task HttpError_RetainsStatusAndBody(int status, bool stream)
    {
        const string raw = "{\"error\":\"原生失败\",\"future\":false}"; var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(raw));
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }));
        var provider = new OllamaLlmProvider(new StubHttpClientFactory(handler));
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () =>
        { if (stream) { await foreach (var _ in provider.StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken)) { } } else await provider.CompleteAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken); });
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed);
    }
    /// <summary>消费者提前终止时释放网络资源，不读完、不补终态、不重试。</summary>
    [Fact]
    public async Task EarlyConsumerDisposal_ReleasesOwnedResources()
    {
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(OllamaFixture.Partial + "\n" + OllamaFixture.Terminal)); using var handler = Handler(body); var factory = new OllamaHttpFactory(handler);
        await using (var enumerator = new OllamaLlmProvider(factory).StreamAsync(OllamaFixture.Payload(), OllamaFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await enumerator.MoveNextAsync()); Assert.False(enumerator.Current.IsTerminal); Assert.False(body.Disposed); }
        Assert.True(body.Disposed); Assert.False(body.Eof); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生媒体类型与 StreamContent 只由响应拥有。</summary>
    private static StubHttpHandler Handler(Stream body, string media = "application/x-ndjson") => new((_, _) =>
    { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(media); return Task.FromResult(response); });
}
