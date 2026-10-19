using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公开 SDK、真实 Gemini 原生适配器和碎片 SSE 验证累计来源/终态边界，不调用搜索服务。</summary>
public sealed class GroundingStreamProviderTests
{
    /// <summary>完整实际请求和有损 Chat 输出对照独立预期；原生来源及未知字段保留在触发事件元数据。</summary>
    [Fact]
    public async Task PublicSdk_ValidNativeStreamMatchesFullGolden()
    {
        var source = GroundingStreamFixtures.Source(); using var handler = Handler(source, verifyRequest: true);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options: GroundingStreamFixtures.Options);
        var actual = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()); var expected = GroundingStreamFixtures.Read("Chat").AsArray();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(actual[i].Data.GetRawText()));
        CustomToolConversionTests.Equal(source[0], JsonNode.Parse(actual[0].Metadata.Conversion!.UpstreamData.GetRawText()));
        Assert.Contains(actual[^1].Metadata.Conversion!.Diagnostics, d => d.Code == "grounding_metadata_not_representable"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>非法全局索引、未解析来源、坏容器/范围和终态尾部经装饰链也不能合成成功。</summary>
    [Theory] [InlineData("global-reference")] [InlineData("missing-source")] [InlineData("metadata-null")] [InlineData("range")]
    [InlineData("foreign-id")] [InlineData("truncated")] [InlineData("late")] [InlineData("native-error")]
    public async Task PublicSdk_InvalidNativeSourceDoesNotSucceed(string kind)
    {
        var source = GroundingStreamFixtures.Source();
        switch (kind)
        {
            case "global-reference": GroundingStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["groundingChunkIndices"]![0] = int.MaxValue; break;
            case "missing-source": GroundingStreamFixtures.Metadata(source, 3)["groundingChunks"] = new JsonArray(); break;
            case "metadata-null": source[1]["candidates"]![0]!["groundingMetadata"] = null; break;
            case "range": GroundingStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["segment"]!["startIndex"] = 4; break;
            case "foreign-id": source[1]["responseId"] = "other"; break;
            case "truncated": source.RemoveAt(3); break;
            case "late": source.Add(source[1].DeepClone().AsObject()); break;
            case "native-error": source.Add(CustomToolFixtures.Parse("""{"error":{"code":500,"message":"fixture"}}""")); break;
        }
        using var handler = Handler(source);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options: GroundingStreamFixtures.Options);
        List<LlmJsonEvent> seen = [];
        if (kind == "native-error")
        {
            var error = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, seen));
            Assert.Contains(error.Diagnostics, d => d.Code == "upstream_stream_error");
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => Collect(client, seen));
        Assert.All(seen, item => Assert.Equal(JsonValueKind.Null, item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>来源/支持共享额度在真实协议适配链上仍是硬边界，不允许丢弃超限记录后发出结束事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicSdk_StateLimitsRemainHard(bool bytes)
    {
        using var handler = Handler(GroundingStreamFixtures.Source());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
            options: GroundingStreamFixtures.Options with { MaxStreamAnnotations = bytes ? 1024 : 1, MaxStreamAnnotationBytes = bytes ? 1 : 1024 * 1024 });
        List<LlmJsonEvent> seen = [];
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, seen));
        Assert.All(seen, item => Assert.Equal(JsonValueKind.Null, item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind));
        Assert.Contains(error.Diagnostics, d => d.Code == (bytes ? "stream_grounding_byte_limit" : "stream_grounding_count_limit")); Assert.Equal(1, handler.Calls);
    }
    /// <summary>新索引检查不等于目标引用流映射，严格模式仍以明确诊断失败。</summary>
    [Fact]
    public async Task PublicSdk_DefaultStrictPolicyDoesNotPretendToMapCitations()
    {
        using var handler = Handler(GroundingStreamFixtures.Source());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options: GroundingStreamFixtures.Options with { AllowLoss = false });
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()));
        Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable"); Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实 Gemini 适配链允许无内容 metadata 尾帧，转换不能提前结束而漏掉原始引用。</summary>
    [Fact]
    public async Task PublicSdk_MetadataOnlyTailRemainsInsideNativeCall()
    {
        using var handler = Handler(GroundingStreamFixtures.MetadataTail());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options: GroundingStreamFixtures.Options);
        var actual = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request());
        var expected = GroundingStreamFixtures.Read("Chat").AsArray(); Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(actual[i].Data.GetRawText()));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>最后一帧同时包含 metadata 和阻断信号时，也不能先把 STOP 转成目标成功终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicSdk_BlockedMetadataTailDoesNotLeakSuccess(bool metadata)
    {
        using var handler = Handler(metadata ? GroundingStreamFixtures.BlockedMetadataTail() : GroundingStreamFixtures.BlockedUsageTail());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options: GroundingStreamFixtures.Options);
        List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(client, seen));
        Assert.All(seen, item => Assert.Equal(JsonValueKind.Null, item.Data.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只使用三字节分片的替代 HTTP；不执行真实 Gemini、Google Search、客户端工具或网络请求。</summary>
    private static StubHttpHandler Handler(IEnumerable<JsonObject> source, bool verifyRequest = false) => new(async (request, token) =>
    {
        if (verifyRequest)
        {
            Assert.Equal("https://wire.invalid/v1/models/model-a:streamGenerateContent?alt=sse", request.RequestUri!.AbsoluteUri);
            Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues("x-goog-api-key"))); Assert.DoesNotContain("bound-key", request.RequestUri.AbsoluteUri);
            CustomToolConversionTests.Equal(GroundingStreamFixtures.Read("GeminiRequest"), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(string.Concat(source.Select(item => "data: " + item.ToJsonString() + "\n\n")))) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    });
    /// <summary>最小 Chat 用户输入，测试只检查响应 grounding，不声称执行了搜索请求转换。</summary>
    private static JsonObject Request() => CustomToolFixtures.Parse("""{"model":"public","messages":[{"role":"user","content":"start"}]}""");
    /// <summary>异常时仍能观察已公开事件，明确断言没有先输出成功 finish_reason。</summary>
    private static async Task Collect(ILokiClient client, List<LlmJsonEvent> seen)
    {
        await foreach (var item in client.LLM.ChatCompletions.StreamAsync(Request().Deserialize<ChatCompletionRequest>()!,
            new() { SessionId = "wire-session" }, TestContext.Current.CancellationToken)) seen.Add(item);
    }
}
