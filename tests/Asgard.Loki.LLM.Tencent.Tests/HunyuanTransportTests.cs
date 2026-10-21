using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>替代 HTTP 和碎片流验证原生错误、目标、EOF、释放与取消边界，不访问网络。</summary>
public sealed class HunyuanTransportTests
{
    /// <summary>HTTP 及 HTTP200 原生 Error 信封保留全部原始字节和媒体类型。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ErrorsRetainOriginalStatusBytesAndMedia(bool native)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var bytes = native ? Encoding.UTF8.GetBytes(HunyuanFixture.Part("native_error").GetRawText()) : new byte[] { 0, 255, 1 }; var status = native ? HttpStatusCode.OK : HttpStatusCode.Unauthorized; var media = native ? "application/problem+json" : "application/octet-stream";
        factory.OnSend = (_, _) => { var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new(media); return Task.FromResult(response); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(status, error.StatusCode); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(media, error.ContentType); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>完整正文必须达到原生各选择的终态，不从 HTTP200 推断完成。</summary>
    [Theory] [InlineData("envelope")] [InlineData("id")] [InlineData("choices")] [InlineData("finish")] [InlineData("message")]
    public async Task IncompleteNativeResultsAreRejected(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var body = JsonNode.Parse(HunyuanFixture.Part("response").GetRawText())!.AsObject(); var response = body["Response"]!.AsObject();
        if (kind == "envelope") body.Remove("Response"); else if (kind == "id") response.Remove("Id"); else if (kind == "choices") response["Choices"] = new JsonArray(); else if (kind == "finish") response["Choices"]![0]!["FinishReason"] = ""; else response["Choices"]![0]!.AsObject().Remove("Message");
        factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Reply(JsonSerializer.SerializeToElement(body))); await Assert.ThrowsAsync<InvalidDataException>(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>成功正文校验 UTF8/JSON/重复属性/媒体类型，SSE 在首次读取前拒绝。</summary>
    [Theory] [InlineData("utf8")] [InlineData("json")] [InlineData("duplicate")] [InlineData("media")] [InlineData("charset")] [InlineData("sse")]
    public async Task InvalidRepresentationsCannotBecomeSuccess(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var bytes = Encoding.UTF8.GetBytes(HunyuanFixture.Part("response").GetRawText()); if (kind == "utf8") bytes = [255]; if (kind == "json") bytes = Encoding.UTF8.GetBytes("{"); if (kind == "duplicate") bytes = Encoding.UTF8.GetBytes("{\"Response\":{},\"Response\":{}}"); var stream = new HunyuanReadStream(bytes);
        factory.OnSend = (_, _) => { var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; result.Content.Headers.TryAddWithoutValidation("Content-Type", kind == "media" ? "text/plain" : kind == "charset" ? "application/json; charset=utf-16" : kind == "sse" ? "text/event-stream" : "application/json"); return Task.FromResult(result); };
        Assert.NotNull(await Record.ExceptionAsync(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken))); if (kind == "sse") Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>EOF、源释放、客户端释放和最终取消均在结果交付前验证。</summary>
    [Theory] [InlineData("ok")] [InlineData("length")] [InlineData("limit")] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")] [InlineData("client")]
    public async Task CompleteDeliveryWaitsForAllResources(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var bytes = Encoding.UTF8.GetBytes(HunyuanFixture.Part("response").GetRawText()); var stream = new HunyuanReadStream(bytes) { FailRead = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        factory.OnSend = (_, _) => { var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; result.Content.Headers.ContentType = new("application/json"); if (kind == "length") result.Content.Headers.ContentLength = bytes.Length + 1; return Task.FromResult(result); };
        if (kind == "client") factory.OnDisposeClient = () => throw new IOException("fixture client disposal"); var provider = new TencentHunyuanProvider(factory, source, new() { MaximumResponseBytes = kind == "limit" ? bytes.Length - 1 : bytes.Length + 1 }, timeProvider: new HunyuanClock());
        var error = await Record.ExceptionAsync(() => provider.CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), cancellation.Token)); if (kind == "ok") { Assert.Null(error); Assert.True(stream.Eof); } else Assert.NotNull(error); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>发送期间的目标、方法、正文、媒体或签名头改变都拒绝，不重试。</summary>
    [Theory] [InlineData("target")] [InlineData("method")] [InlineData("content")] [InlineData("type")] [InlineData("host")] [InlineData("token")] [InlineData("response-target")]
    public async Task ActualWireMutationFails(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); factory.OnSend = (message, _) =>
        {
            if (kind == "target") message.RequestUri = new("https://different.example.invalid/"); if (kind == "method") message.Method = HttpMethod.Get; if (kind == "content") message.Content = new StringContent("{}"); if (kind == "type") message.Content!.Headers.ContentType = new("text/plain"); if (kind == "host") message.Headers.Host = "different.example.invalid"; if (kind == "token") message.Headers.Add("X-TC-Token", "unselected-token"); var response = HunyuanFixture.Reply(HunyuanFixture.Part("response")); if (kind == "response-target") response.RequestMessage = new(HttpMethod.Post, "https://different.example.invalid/"); return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>工厂默认认证和 X-TC 配置不能污染本次签名身份。</summary>
    [Theory] [InlineData("Authorization")] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("Host")] [InlineData("X-TC-Token")]
    public async Task FactoryAuthenticationDefaultsCannotBeBorrowed(string header)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory { ConfigureClient = client => client.DefaultRequestHeaders.TryAddWithoutValidation(header, "fixture-default") }; await Assert.ThrowsAsync<InvalidOperationException>(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>原生数字 ErrorMsg 与云错误字符串 Code 不混淆，保留完整错误或零码元数据。</summary>
    [Theory] [InlineData("0")] [InlineData("4001")] [InlineData("\"4001\"")]
    public async Task NativeNumericErrorMsgIsKeptDistinct(string code)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var body = JsonNode.Parse(HunyuanFixture.Part("response").GetRawText())!.AsObject(); body["Response"]!["ErrorMsg"] = JsonNode.Parse("{\"Code\":" + code + ",\"Msg\":\"native status\",\"Future\":false}"); var data = JsonSerializer.SerializeToElement(body); factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Reply(data));
        var provider = new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()); if (code == "0") Assert.True(JsonElement.DeepEquals(data, (await provider.CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)).Data));
        else { var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => provider.CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.True(JsonElement.DeepEquals(data, JsonSerializer.Deserialize<JsonElement>(error.Body.Span))); } Assert.Equal(1, factory.Calls);
    }
    /// <summary>读取/客户端关闭边界的延迟请求修改也不能交付完整结果。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateWireMutationCannotPublishAfterClosure(bool clientClosure)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); HttpRequestMessage? sent = null;
        var stream = new HunyuanReadStream(Encoding.UTF8.GetBytes(HunyuanFixture.Part("response").GetRawText())) { OnEof = clientClosure ? null : () => sent!.Headers.Host = "changed.example.invalid" };
        if (clientClosure) factory.OnDisposeClient = () => sent!.RequestUri = new("https://changed.example.invalid/");
        factory.OnSend = (message, _) => { sent = message; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
}
