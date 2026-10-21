using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>文本补全的本地字节/原生形状策略与固定 Session，不探测模型、不自动修复请求。</summary>
public sealed class AzureTextPolicyTests
{
    /// <summary>最终请求字节以已选择的部署位置计算，恰到上限成功，少一字节在认证/工厂之前失败。</summary>
    [Theory] [InlineData(0, false, false)] [InlineData(0, false, true)] [InlineData(0, true, false)] [InlineData(0, true, true)]
    [InlineData(2, false, false)] [InlineData(2, false, true)] [InlineData(2, true, false)] [InlineData(2, true, true)]
    public async Task ExactFinalRequestByteBound(int profile, bool streaming, bool below)
    {
        var raw = (profile == 2 ? "{\"prompt\":[42,0],\"stream\":" : "{\"model\":\"deploy.text.1\",\"prompt\":[42,0],\"stream\":") + (streaming ? "true}" : "false}"); var expected = Encoding.UTF8.GetBytes(raw); var payload = JsonNode.Parse("{\"model\":\"public\",\"prompt\":[42,0]}")!.AsObject();
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory(); using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse())); factory.OnSend = async (message, token) => { Assert.Equal(expected, await message.Content!.ReadAsByteArrayAsync(token)); return streaming ? AzureTextFixture.Streaming(body) : AzureTextFixture.Json(AzureTextFixture.Part("response")); };
        var provider = new AzureLlmProvider(factory, AzureTextFixture.Options(profile) with { MaximumTextCompletionRequestBytes = expected.Length - (below ? 1 : 0) }, source, timeProvider: clock); var token = TestContext.Current.CancellationToken;
        async Task Invoke() { if (streaming) await AzureTextFixture.Drain(((ITextCompletionsAdapter)provider).StreamAsync(payload, AzureTextFixture.Context(true, profile), token), token); else await ((ITextCompletionsAdapter)provider).CompleteAsync(payload, AzureTextFixture.Context(true, profile), token); }
        if (below) await Assert.ThrowsAsync<ArgumentException>(Invoke); else await Invoke(); Assert.Equal(below ? 0 : 1, source.Calls); Assert.Equal(below ? 0 : 1, factory.Creates); Assert.Equal(below ? 0 : 1, factory.Calls);
    }
    /// <summary>不合法原生 prompt 与字段别名在认证等待前失败；不从坏输入推断其他协议。</summary>
    [Theory] [InlineData("false")] [InlineData("42")] [InlineData("{}")] [InlineData("[42,-1]")] [InlineData("[1.5]")] [InlineData("[9007199254740993]")]
    [InlineData("[\"a\",42]")] [InlineData("[[42],[\"wrong\"]]")] [InlineData("[\"a\",null]")] [InlineData("MODEL")] [InlineData("STREAM")]
    public async Task InvalidNativeRequestNeverAcquiresToken(string json)
    {
        var payload = AzureTextFixture.Payload(); if (json is "MODEL" or "STREAM") payload[json] = false; else payload["prompt"] = JsonNode.Parse(json);
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory(); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await Assert.ThrowsAsync<ArgumentException>(() => ((ITextCompletionsAdapter)provider).CompleteAsync(payload, AzureTextFixture.Context(true), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>认证源开始等待前的深快照防止调用者 DOM 在之后被修改。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotPrecedesTokenSourceAwait(bool streaming)
    {
        var payload = AzureTextFixture.Payload(streaming); var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory(); using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse()));
        source.Resolve = (principal, _) => { payload.Clear(); return ValueTask.FromResult(new AzureLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); }; factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(AzureTextFixture.Part(streaming ? "v1_stream" : "v1_complete"), AzureCompactionFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return streaming ? AzureTextFixture.Streaming(body) : AzureTextFixture.Json(AzureTextFixture.Part("response")); };
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock); var token = TestContext.Current.CancellationToken;
        if (streaming) await AzureTextFixture.Drain(((ITextCompletionsAdapter)provider).StreamAsync(payload, AzureTextFixture.Context(true), token), token); else await ((ITextCompletionsAdapter)provider).CompleteAsync(payload, AzureTextFixture.Context(true), token); Assert.Empty(payload); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>非正策略构造失败；省略策略保持旧默认指纹，显式策略进入身份。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidPolicyFailsImmediately(int maximum)
    { using var factory = AzureTextFixture.Factory(); Assert.Throws<ArgumentOutOfRangeException>(() => new AzureLlmProvider(factory, new() { MaximumTextCompletionRequestBytes = maximum })); Assert.Equal(0, factory.Creates); }
    /// <summary>默认新策略不进入序列化，显式有效默认值也不能借用既有 Session。</summary>
    [Fact]
    public async Task ExplicitPolicyInvalidatesOldSession()
    {
        using var factory = AzureTextFixture.Factory(); var store = AzureFixture.WebSocketStore(); var firstProvider = new AzureLlmProvider(factory); Assert.DoesNotContain("MaximumTextCompletionRequestBytes", firstProvider.BindingIdentity);
        await using (var first = AzureTextFixture.Build(firstProvider, store: store)) await first.LLM.TextCompletions.CompleteAsync(AzureTextFixture.Request(), AzureTextFixture.Call(), TestContext.Current.CancellationToken);
        var secondProvider = new AzureLlmProvider(factory, new() { MaximumTextCompletionRequestBytes = 32 * 1024 * 1024 }); Assert.NotEqual(firstProvider.BindingIdentity, secondProvider.BindingIdentity); await using var second = AzureTextFixture.Build(secondProvider, store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.TextCompletions.CompleteAsync(AzureTextFixture.Request(), AzureTextFixture.Call(), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>缓存重建和目标重排保留服务器/部署/认证，文本 ID 不变成 Responses 归属。</summary>
    [Fact]
    public async Task RebuiltSessionRetainsWholeTargetAndNativeIdBoundary()
    {
        using var factory = AzureTextFixture.Factory(); var store = AzureFixture.WebSocketStore(); var a = AzureTextFixture.Target(); var b = a with { Id = "other", BaseAddress = new("https://other.invalid/openai/v1/"), UpstreamModel = "other-model" }; factory.OnSend = (message, _) => { Assert.Equal("azure-text.example.invalid", message.RequestUri!.Host); Assert.Equal("fixture-key", Assert.Single(message.Headers.GetValues("api-key"))); return Task.FromResult(AzureTextFixture.Json(AzureTextFixture.Part("response"))); };
        await using (var first = AzureTextFixture.Build(new AzureLlmProvider(factory), store: store, targets: [a, b])) await first.LLM.TextCompletions.CompleteAsync(AzureTextFixture.Request(), AzureTextFixture.Call(), TestContext.Current.CancellationToken);
        await using var second = AzureTextFixture.Build(new AzureLlmProvider(factory), store: store, targets: [b, a]); var result = await second.LLM.TextCompletions.CompleteAsync(AzureTextFixture.Request(), AzureTextFixture.Call(), TestContext.Current.CancellationToken);
        var binding = await store.GetAsync("text-session", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal("primary", binding.TargetId); Assert.False(await store.OwnsResponseAsync(binding, result.Data.GetProperty("id").GetString()!, TestContext.Current.CancellationToken)); Assert.Equal(2, factory.Calls);
    }
    /// <summary>无效完整原生结果在已关闭资源后失败，不把 Chat 或不完整 choices 当作文本补全。</summary>
    [Theory] [InlineData("id")] [InlineData("model")] [InlineData("created")] [InlineData("object")] [InlineData("index")] [InlineData("text")] [InlineData("finish_reason")] [InlineData("logprobs")]
    public async Task InvalidFullNativeShapeCannotPublish(string field)
    {
        var raw = JsonNode.Parse(AzureTextFixture.Part("response").GetRawText())!.AsObject(); if (field is "id" or "model" or "created" or "object") raw[field] = null; else raw["choices"]![0]![field] = field == "index" ? JsonValue.Create(-1) : field == "logprobs" ? new JsonArray() : null;
        using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Json(AzureCompactionFixture.Element(raw.ToJsonString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }
    /// <summary>根原生错误在 HTTP200/失败状态保留原字节，不因纯文本媒体或未知字段丢失。</summary>
    [Theory] [InlineData(200)] [InlineData(400)] [InlineData(429)] [InlineData(503)]
    public async Task NativeErrorEnvelopeRetainsRawBody(int status)
    {
        var raw = AzureTextFixture.Part("native_error").GetRawText(); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(raw, Encoding.UTF8, "text/plain") });
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw), error.Body.ToArray()); Assert.Equal("text/plain; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
    /// <summary>文档化的平铺 code/message 错误不会误判为原生生成结果。</summary>
    [Fact]
    public async Task FlatNativeErrorRemainsUpstreamError()
    {
        const string raw = "{\"code\":\"native-code\",\"message\":\"native failure\",\"future\":9007199254740993}"; using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Json(AzureCompactionFixture.Element(raw)));
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, factory.Calls);
    }
    /// <summary>正常完整 choices 与未知根 code/message 同时存在时不误报为平铺错误。</summary>
    [Fact]
    public async Task ValidCompletionKeepsUnknownCodeAndMessage()
    {
        var raw = JsonNode.Parse(AzureTextFixture.Part("response").GetRawText())!.AsObject(); raw["code"] = "business-code"; raw["message"] = "business-message"; using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureTextFixture.Json(AzureCompactionFixture.Element(raw.ToJsonString())));
        var result = await AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(AzureCompactionFixture.Element(raw.ToJsonString()), result.Data)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>处理器原地改写已发送消息也不能绕过完整目标/方法检查，且读取正文前失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task InPlaceActualRequestDriftCannotPublish(bool streaming, bool method)
    {
        using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(streaming ? AzureTextFixture.Sse() : AzureTextFixture.Part("response").GetRawText())); using var factory = AzureTextFixture.Factory(); factory.OnSend = (request, _) =>
        { if (method) request.Method = HttpMethod.Get; else request.RequestUri = new("https://different.invalid/completions"); var reply = streaming ? AzureTextFixture.Streaming(body) : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) }; reply.RequestMessage = request; return Task.FromResult(reply); };
        var provider = (ITextCompletionsAdapter)new AzureLlmProvider(factory); var token = TestContext.Current.CancellationToken;
        if (streaming) await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Drain(provider.StreamAsync(AzureTextFixture.Payload(true), AzureTextFixture.Context(), token), token)); else await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompleteAsync(AzureTextFixture.Payload(), AzureTextFixture.Context(), token));
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls);
    }

    /// <summary>不可解析或负数/多值长度声明在正文读取之前失败，不能当作未知长度跳过。</summary>
    [Theory] [InlineData("not-a-length")] [InlineData("-1")] [InlineData("1, 2")]
    public async Task InvalidDeclaredLengthFailsBeforeRead(string length)
    {
        using var body = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Part("response").GetRawText())); using var factory = AzureTextFixture.Factory(); factory.OnSend = (_, _) => { var content = new StreamContent(body); Assert.True(content.Headers.TryAddWithoutValidation("Content-Length", length)); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureTextFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.Equal(1, factory.Disposals);
    }

}
