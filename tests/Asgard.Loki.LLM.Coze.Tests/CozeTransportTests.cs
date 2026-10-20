using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>原生 code/原始字节/实际 HTTP 及资源故障，不隐藏错误、不追加查询或重试。</summary>
public sealed class CozeTransportTests
{
    /// <summary>实际 HTTP 二进制错误和 HTTP 200 原生 code 错误保留完整原文/媒体/状态。</summary>
    [Theory] [InlineData("binary")] [InlineData("native")] [InlineData("native-plain")]
    public async Task RawErrors_ArePreserved(string kind)
    {
        byte[] bytes = kind == "binary" ? [0xff, 0, 0x80] : Encoding.UTF8.GetBytes("{\"code\":701231,\"msg\":\"native error\",\"detail\":{\"logid\":\"native-log\"},\"future\":false}");
        var media = kind == "binary" ? "application/octet-stream" : kind == "native-plain" ? "text/plain" : "application/json"; var body = new CozeBodyStream(bytes);
        using var handler = Reply(body, media, kind == "binary" ? 429 : 200); using var factory = new CozeHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, failure.Body.ToArray()); Assert.Equal(kind == "binary" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, failure.StatusCode); Assert.Equal(media, failure.ContentType); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>缺少整数 code/原生 Chat 数据或原始坏编码/重复属性等不被默认成功值掩盖。</summary>
    [Theory] [InlineData("code-missing")] [InlineData("code-type")] [InlineData("code-fraction")]
    [InlineData("data-null")] [InlineData("id-missing")] [InlineData("status-type")] [InlineData("duplicate")]
    [InlineData("unicode")] [InlineData("utf8")] [InlineData("truncated")]
    public async Task MalformedNativeResult_FailsExplicitly(string kind)
    {
        var json = kind switch
        {
            "code-missing" => "{\"data\":{}}", "code-type" => "{\"code\":\"0\",\"data\":{}}", "code-fraction" => "{\"code\":0.5,\"data\":{}}", "data-null" => "{\"code\":0,\"data\":null}",
            "id-missing" => "{\"code\":0,\"data\":{\"conversation_id\":\"x\",\"status\":\"in_progress\"}}", "status-type" => Part("ack").GetRawText().Replace("\"in_progress\"", "false", StringComparison.Ordinal),
            "duplicate" => Part("ack").GetRawText()[..^1] + ",\"extra\":{\"x\":1,\"x\":2}}", "unicode" => Part("ack").GetRawText()[..^1] + ",\"extra\":\"\\ud800\"}", _ => Part("ack").GetRawText()[..^1]
        };
        byte[] bytes = kind == "utf8" ? [.. Encoding.UTF8.GetBytes(json), 0xff] : Encoding.UTF8.GetBytes(json);
        using var handler = Reply(new CozeBodyStream(bytes)); using var factory = new CozeHttpFactory(handler); var failure = await Record.ExceptionAsync(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.True(failure is InvalidDataException or JsonException, failure?.ToString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生请求/回执预算按确切 UTF-8 字节计数，超限不截断、不重试。</summary>
    [Theory] [InlineData(false, 0)] [InlineData(false, -1)] [InlineData(true, 0)] [InlineData(true, -1)]
    public async Task ExactByteBudget_IsEnforced(bool request, int adjustment)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(Part("wire_complete").GetRawText())!.ToJsonString(); var bytes = Encoding.UTF8.GetBytes(Part("ack").GetRawText());
        var options = request ? Options() with { MaximumRequestBytes = Encoding.UTF8.GetByteCount(wire) + adjustment } : Options() with { MaximumResponseBytes = bytes.Length + adjustment };
        var body = new CozeBodyStream(bytes); using var handler = Reply(body); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, options, Token);
        if (adjustment == 0) await client.CreateAsync(Request(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidDataException>(() => client.CreateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(request && adjustment < 0 ? 0 : 1, handler.Calls); Assert.Equal(!request || adjustment == 0, body.Disposed);
    }
    /// <summary>声明的短读、长读、坏值及超预算在正常回执之前失败。</summary>
    [Theory] [InlineData("short")] [InlineData("long")] [InlineData("bad")] [InlineData("over")]
    public async Task DeclaredLengthMismatch_PreventsResult(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(Part("ack").GetRawText()); var body = new CozeBodyStream(bytes); using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (kind == "bad") response.Content.Headers.TryAddWithoutValidation("Content-Length", "bad"); else response.Content.Headers.ContentLength = kind == "over" ? long.MaxValue : bytes.Length + (kind == "short" ? -1 : 1); return Task.FromResult(response);
        });
        using var factory = new CozeHttpFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.True(body.Disposed); if (kind is "bad" or "over") Assert.Equal(0, body.ReadCalls);
    }
    /// <summary>EOF、流/客户端释放异常和最终取消都阻止返回看似有效的回执。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")] [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task LateFailure_PreventsAcknowledgement(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Part("ack").GetRawText()))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = Reply(body); using var factory = new CozeHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("fixture client") : kind == "cancel-client" ? cancel.Cancel : null };
        var failure = await Record.ExceptionAsync(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), cancel.Token));
        if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>请求/响应实际方法及地址在派发、EOF、客户端释放时都不能漂移。</summary>
    [Theory] [InlineData("dispatch-uri")] [InlineData("dispatch-method")] [InlineData("response-uri")] [InlineData("eof-uri")] [InlineData("dispose-uri")]
    public async Task ActualTargetDrift_IsRejected(string kind)
    {
        HttpRequestMessage? sent = null; var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Part("ack").GetRawText())) { OnEof = kind == "eof-uri" ? () => sent!.RequestUri = new("https://other.invalid/chat") : null };
        using var handler = new StubHttpHandler((message, _) =>
        {
            sent = message; if (kind == "dispatch-uri") message.RequestUri = new("https://other.invalid/chat"); if (kind == "dispatch-method") message.Method = HttpMethod.Get;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; if (kind == "response-uri") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/chat"); return Task.FromResult(response);
        });
        using var factory = new CozeHttpFactory(handler) { OnClientDispose = kind == "dispose-uri" ? () => sent!.RequestUri = new("https://other.invalid/chat") : null };
        await Assert.ThrowsAsync<InvalidDataException>(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>预取消不创建客户端；原生 SSE 不能从 JSON 方法隐式接入。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrWrongProtocol_FailsBeforeReading(bool cancellation)
    {
        using var cancel = new CancellationTokenSource(); if (cancellation) cancel.Cancel(); var body = new CozeBodyStream([1, 2]); using var handler = Reply(body, "text/event-stream"); using var factory = new CozeHttpFactory(handler);
        var failure = await Record.ExceptionAsync(() => new CozeChatClient(factory, Options(), Token).CreateAsync(Request(), cancel.Token));
        if (cancellation) { Assert.IsAssignableFrom<OperationCanceledException>(failure); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls); } else Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, body.ReadCalls);
    }
}
