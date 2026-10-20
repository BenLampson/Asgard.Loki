using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>原生压缩请求/完整窗口的严格边界，不把截断、别名或未知非法字符串当作有效窗口。</summary>
public sealed class AzureCompactionBoundaryTests
{
    /// <summary>非法本地字段在认证源和 HTTP 工厂之前失败。</summary>
    [Theory] [InlineData("stream")] [InlineData("Stream")] [InlineData("MODEL")] [InlineData("input")]
    [InlineData("previous-number")] [InlineData("previous-empty")] [InlineData("unicode-value")] [InlineData("unicode-key")]
    public async Task InvalidRequestFailsBeforeAuthentication(string kind)
    {
        var request = AzureCompactionFixture.Payload();
        switch (kind)
        {
            case "stream": case "Stream": request[kind] = false; break;
            case "MODEL": request[kind] = "alias"; break;
            case "input": request[kind] = 42; break;
            case "previous-number": request["previous_response_id"] = 42; break;
            case "previous-empty": request["previous_response_id"] = " "; break;
            case "unicode-key": request["\ud800"] = false; break;
            default: request["future"] = "\ud800"; break;
        }
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory(); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        var error = await Record.ExceptionAsync(() => provider.CompactAsync(request, AzureCompactionFixture.Context(true), TestContext.Current.CancellationToken));
        Assert.NotNull(error); if (kind.StartsWith("unicode", StringComparison.Ordinal)) Assert.IsType<EncoderFallbackException>(error); else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>原生可空/省略输入和空字符串不被填充或删除，版本仅编码为一个查询值。</summary>
    [Theory] [InlineData("absent")] [InlineData("null")] [InlineData("text")] [InlineData("array")]
    public async Task OptionalNativeInputIsNotCoerced(string kind)
    {
        var payload = new JsonObject { ["future"] = false }; if (kind != "absent") payload["input"] = kind switch { "text" => JsonValue.Create(""), "array" => new JsonArray(), _ => null };
        using var factory = AzureCompactionFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal("https://azure-compact.example.invalid/proxy/openai/v1/responses/compact?api-version=a%2Bb%26x%3Dy", message.RequestUri!.AbsoluteUri);
            var raw = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject(); Assert.Equal(kind != "absent", raw.ContainsKey("input")); if (kind != "absent") Assert.True(JsonNode.DeepEquals(payload["input"], raw["input"])); Assert.False(raw.ContainsKey("stream")); return AzureCompactionFixture.Json(AzureCompactionFixture.Part("response"));
        };
        await new AzureLlmProvider(factory, new() { ApiVersion = "a+b&x=y" }).CompactAsync(payload, AzureCompactionFixture.Context(), TestContext.Current.CancellationToken);
    }
    /// <summary>请求上限以最终实际模型映射后的 UTF-8 字节计数，超限不取得令牌。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalRequestByteBoundary(bool below)
    {
        var payload = new JsonObject { ["model"] = "public", ["input"] = "中文", ["future"] = false };
        var expected = Encoding.UTF8.GetBytes("{\"model\":\"deploy.compact.1\",\"input\":\"\\u4E2D\\u6587\",\"future\":false}");
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory(); factory.OnSend = async (message, token) =>
        { Assert.Equal(expected, await message.Content!.ReadAsByteArrayAsync(token)); return AzureCompactionFixture.Json(AzureCompactionFixture.Part("response")); };
        var provider = new AzureLlmProvider(factory, new() { MaximumCompactionRequestBytes = expected.Length - (below ? 1 : 0) }, source, timeProvider: clock);
        if (below) { await Assert.ThrowsAsync<ArgumentException>(() => provider.CompactAsync(payload, AzureCompactionFixture.Context(true), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates); }
        else { await provider.CompactAsync(payload, AzureCompactionFixture.Context(true), TestContext.Current.CancellationToken); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); }
    }
    /// <summary>不是完整原生压缩窗口的成功 HTTP 不能交付为压缩结果。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"object\":\"response\",\"output\":[]}")]
    [InlineData("{\"object\":\"response.compaction\"}")] [InlineData("{\"object\":\"response.compaction\",\"output\":{}}")]
    public async Task InvalidNativeWindowCannotPublish(string json)
    {
        using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureCompactionFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>HTTP200 根级错误优先保留原文字节，即使上游错误地标记为纯文本。</summary>
    [Fact]
    public async Task NativeErrorInPlainTextRetainsOriginalData()
    {
        var bytes = Encoding.UTF8.GetBytes(AzureCompactionFixture.Part("native_error").GetRawText()); using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (_, _) =>
        { var content = new ByteArrayContent(bytes); content.Headers.TryAddWithoutValidation("Content-Type", "text/plain"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => AzureCompactionFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal("text/plain", error.ContentType); Assert.Equal(HttpStatusCode.OK, error.StatusCode);
    }
    /// <summary>处理器原地改写本次消息地址也不能绕过固定目标检查。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InPlaceRequestMutationIsDetected(bool method)
    {
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureCompactionFixture.Part("response").GetRawText())); using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (request, _) =>
        { if (method) request.Method = HttpMethod.Get; else request.RequestUri = new("https://other.invalid/responses/compact"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream), RequestMessage = request }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => AzureCompactionFixture.Invoke(new AzureLlmProvider(factory), TestContext.Current.CancellationToken)); Assert.False(stream.Eof); Assert.True(stream.Disposed);
    }
}
