using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>经公共 Gemini SDK 入口和原生 OpenAI 适配器验证完整反向引用转换。</summary>
public sealed class GeminiCitationProviderTests
{
    /// <summary>比较独立最终请求与响应，完整保留原始元数据且只发出一次 HTTP 调用。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public async Task CompletePipeline_MatchesAuthoredGolden(LlmProtocol upstream, LlmCitationIndexUnit unit)
    {
        var source = GeminiCitationFixtures.Read(upstream.ToString() + unit);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://wire.invalid/v1/" + (upstream == LlmProtocol.Responses ? "responses" : "chat/completions"), request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("bound-key", request.Headers.Authorization.Parameter);
            CustomToolConversionTests.Equal(GeminiCitationFixtures.Read("Request" + upstream), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(source.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, upstream, options: GeminiGroundingFixtures.Options(unit));
        var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, GeminiCitationFixtures.Read("Request"));
        CustomToolConversionTests.Equal(GeminiCitationFixtures.Read("Gemini" + upstream), JsonNode.Parse(result.Data.GetRawText()));
        Assert.Empty(result.Metadata.Conversion!.Diagnostics);
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        Assert.Equal(LlmProtocol.Gemini, result.Metadata.Conversion.PublicProtocol); Assert.Equal(upstream, result.Metadata.Conversion.UpstreamProtocol);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>未声明单位、坏范围与不可表达来源在实际适配链仍严格失败，不自动重试。</summary>
    [Theory] [InlineData("unit")] [InlineData("range")] [InlineData("source")]
    public async Task InvalidOrUnrepresentableReply_FailsWithoutRetry(string kind)
    {
        var source = GeminiCitationFixtures.Read("ResponsesUtf8Byte");
        var citation = GeminiCitationFixtures.Citations(source, LlmProtocol.Responses).First();
        if (kind == "range") citation["end_index"] = 4;
        if (kind == "source") citation["url"] = "file:///private/a";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(source.ToJsonString(), Encoding.UTF8, "application/json") }));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses,
            options: GeminiGroundingFixtures.Options(kind == "unit" ? LlmCitationIndexUnit.Unspecified : LlmCitationIndexUnit.Utf8Byte));
        if (kind == "range") await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, GeminiCitationFixtures.Read("Request")));
        else
        {
            var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, GeminiCitationFixtures.Read("Request")));
            Assert.Contains(error.Diagnostics, d => d.Code == (kind == "unit" ? "search_citations_not_representable" : "grounding_source_not_representable"));
        }
        Assert.Equal(1, handler.Calls);
    }
}
