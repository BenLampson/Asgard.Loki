using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过实际 Google 适配器及公共 SDK 校验完整 grounding，并独立验证流式边界。</summary>
public sealed class GeminiGroundingProviderTests
{
    /// <summary>六个最终完整结果与 HTTP 请求快照，保留原始上游元数据且只发送一次。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public async Task CompletePipeline_MatchesAuthoredGolden(LlmProtocol source, LlmCitationIndexUnit unit)
    {
        var raw = GeminiGroundingFixtures.Read();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            CustomToolConversionTests.Equal(CustomToolFixtures.Parse("""{"contents":[{"role":"user","parts":[{"text":"start"}]}]}"""),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            Assert.Contains("model-a:generateContent", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        await using var client = ConvertingProviderTests.Build(handler, source, LlmProtocol.Gemini, options: GeminiGroundingFixtures.Options(unit));
        var result = await ConvertingProviderTests.CompleteAsync(client, source, Request(source));
        CustomToolConversionTests.Equal(GeminiGroundingFixtures.Read(source.ToString() + unit), JsonNode.Parse(result.Data.GetRawText()));
        Assert.Empty(result.Metadata.Conversion!.Diagnostics); CustomToolConversionTests.Equal(raw, JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>真实适配链上的查询元数据也需要显式损耗许可，完整原文可供调用者自行展示。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletePipeline_QueryLossIsExplicit(bool allowLoss)
    {
        var raw = GeminiGroundingFixtures.Read(); GeminiGroundingFixtures.Metadata(raw)["webSearchQueries"] = new JsonArray("test");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(raw.ToJsonString(), Encoding.UTF8, "application/json") }));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini,
            options: GeminiGroundingFixtures.Options() with { AllowLoss = allowLoss });
        if (!allowLoss) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, Request(LlmProtocol.Responses)));
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, Request(LlmProtocol.Responses));
            Assert.Equal("grounding_metadata_field_not_representable", Assert.Single(result.Metadata.Conversion!.Diagnostics).Code);
            CustomToolConversionTests.Equal(raw, JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>完整映射不虚构 Gemini 流式引用 delta；声明单位不能绕过严格流边界。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamPipeline_KeepsExplicitGroundingBoundary(bool allowLoss)
    {
        var events = HostedSearchFixtures.TextStream(LlmProtocol.Gemini);
        events.Last()["candidates"]![0]!["groundingMetadata"] = GeminiGroundingFixtures.Metadata(GeminiGroundingFixtures.Read()).DeepClone();
        using var handler = new StubHttpHandler((_, _) =>
        {
            var body = string.Concat(events.Select(e => "data: " + e.ToJsonString() + "\n\n"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini,
            options: GeminiGroundingFixtures.Options() with { AllowLoss = allowLoss });
        if (!allowLoss)
        {
            var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, Request(LlmProtocol.Responses)));
            Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
        }
        else
        {
            var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, Request(LlmProtocol.Responses));
            Assert.Contains(result.Last().Metadata.Conversion!.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
            Assert.Equal("response.completed", result.Last().Data.GetProperty("type").GetString());
            Assert.All(result, e => Assert.DoesNotContain("url_citation", e.Data.GetRawText()));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>最小文本请求，无真实 Key 或外部搜索依赖。</summary>
    private static JsonObject Request(LlmProtocol source) => CustomToolFixtures.Parse(source == LlmProtocol.ChatCompletions
        ? """{"model":"public","messages":[{"role":"user","content":"start"}]}""" : """{"model":"public","input":"start"}""");
}
