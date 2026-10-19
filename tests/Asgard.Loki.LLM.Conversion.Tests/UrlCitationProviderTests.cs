using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过实际公共 SDK 和原生 HTTP 适配器验证引用结果及流式边界。</summary>
public sealed class UrlCitationProviderTests
{
    /// <summary>独立最终请求和完整返回快照，核对原始上游元数据及单次调用。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public async Task PublicCompleteCall_PreservesCitationsAndRawMetadata(LlmProtocol source)
    {
        var upstream = UrlCitationFixtures.Other(source); var raw = UrlCitationFixtures.Read(upstream.ToString());
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = CustomToolFixtures.Parse(upstream == LlmProtocol.Responses
                ? """{"model":"model-a","stream":false,"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"start"}]}]}"""
                : """{"model":"model-a","stream":false,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}]}""");
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            Assert.Equal("bound-key", request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        await using var client = ConvertingProviderTests.Build(handler, source, upstream);
        var result = await ConvertingProviderTests.CompleteAsync(client, source, Request(source));
        CustomToolConversionTests.Equal(UrlCitationFixtures.Read("Expected" + source), JsonNode.Parse(result.Data.GetRawText()));
        Assert.Empty(result.Metadata.Conversion!.Diagnostics); CustomToolConversionTests.Equal(raw, JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>真实适配链上的分片 SSE 保持严格失败或显式诊断，不把完整映射误宣称为流式支持。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicStream_RequiresExplicitCitationLoss(bool allowLoss)
    {
        using var handler = new StubHttpHandler((_, _) =>
        {
            var body = string.Concat(UrlCitationFixtures.Stream().Select(e => "data: " + e.ToJsonString() + "\n\n"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses, options: new() { AllowLoss = allowLoss });
        if (!allowLoss)
        {
            var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request(LlmProtocol.ChatCompletions)));
            Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable");
        }
        else
        {
            var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request(LlmProtocol.ChatCompletions));
            Assert.Contains(result.Last().Metadata.Conversion!.Diagnostics, d => d.Code == "search_citations_not_representable");
            Assert.Contains("stop", result.Last().Data.GetRawText()); Assert.All(result, e => Assert.DoesNotContain("source.invalid", e.Data.GetRawText()));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>只使用最小文本请求，避免用搜索声明或真实 Key 伪装成本次测试依赖。</summary>
    private static JsonObject Request(LlmProtocol source) => CustomToolFixtures.Parse(source == LlmProtocol.ChatCompletions
        ? """{"model":"public","messages":[{"role":"user","content":"start"}]}""" : """{"model":"public","input":"start"}""");
}
