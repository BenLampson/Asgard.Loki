using System.Net;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公共 SDK、真实原生适配器与碎片化离线 SSE 验证引用状态校验不能在装饰链上丢失。</summary>
public sealed class AnnotationStreamProviderTests
{
    /// <summary>完整原生请求和 Chat 目标事件与独立预期比较，引用损耗仍显示且源最终数据完整保留。</summary>
    [Fact]
    public async Task PublicSdk_ValidLossyStreamMatchesGolden()
    {
        var source = AnnotationStreamFixtures.Read();
        using var handler = Handler(source, checkRequest: true);
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses, options: AnnotationStreamFixtures.Options);
        var output = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()); var expected = AnnotationStreamFixtures.Read("Chat");
        Assert.Equal(expected.Count, output.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(output[i].Data.GetRawText()));
        CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(output[^1].Metadata.Conversion!.UpstreamData.GetRawText()));
        Assert.Contains(output[^1].Metadata.Conversion!.Diagnostics, d => d.Code == "search_citations_not_representable"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>错配、快照变化、Unicode 错误、截断和迟到事件经公共 SDK 也不能返回成功终态或自动重发。</summary>
    [Theory] [InlineData("foreign-item")] [InlineData("part-mismatch")] [InlineData("final-mismatch")]
    [InlineData("unicode")] [InlineData("truncated")] [InlineData("late")]
    public async Task PublicSdk_InvalidNativeStreamFailsWithoutRetry(string kind)
    {
        var source = AnnotationStreamFixtures.Read(); var annotation = source.Single(e => e["type"]!.GetValue<string>() == "response.output_text.annotation.added");
        switch (kind)
        {
            case "foreign-item": annotation["item_id"] = "other"; break;
            case "part-mismatch": annotation["content_index"] = 0; break;
            case "final-mismatch": source[^1]["response"]!["output"]![0]!["content"]![1]!["annotations"]![0]!["title"] = "changed"; break;
            case "unicode": foreach (var citation in AnnotationStreamFixtures.Citations(source)) citation["start_index"] = 2; break;
            case "truncated": source.RemoveAt(source.Count - 1); break;
            case "late": source.Add(annotation.DeepClone().AsObject()); AnnotationStreamFixtures.Sequence(source); break;
        }
        using var handler = Handler(source); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses, options: AnnotationStreamFixtures.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>没有损耗许可仍按既有引用边界失败，新增校验不是引用流映射的支持声明。</summary>
    [Fact]
    public async Task PublicSdk_StrictModeStillRejectsUnrepresentableCitationDelta()
    {
        using var handler = Handler(AnnotationStreamFixtures.Read());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            options: AnnotationStreamFixtures.Options with { AllowLoss = false });
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()));
        Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>实际适配链上的引用资源超限同样不能通过损耗许可绕过。</summary>
    [Fact]
    public async Task PublicSdk_ByteLimitIsHardFailure()
    {
        using var handler = Handler(AnnotationStreamFixtures.Read());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            options: AnnotationStreamFixtures.Options with { MaxStreamAnnotationBytes = 1 });
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request()));
        Assert.Contains(error.Diagnostics, d => d.Code == "stream_annotation_byte_limit"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>真实适配链上的明确不完整终态保留 length 和全部源引用元数据，不伪造 stop。</summary>
    [Fact]
    public async Task PublicSdk_IncompleteWithAnnotationsRetainsLength()
    {
        var source = AnnotationStreamFixtures.Read(); source[^1]["type"] = "response.incomplete"; source[^1]["response"]!["status"] = "incomplete";
        source[^1]["response"]!["incomplete_details"] = CustomToolFixtures.Parse("""{"reason":"max_output_tokens"}""");
        source[^1]["response"]!["output"]![0]!["status"] = "incomplete";
        source.Single(e => e["type"]!.GetValue<string>() == "response.output_item.done")["item"]!["status"] = "incomplete";
        using var handler = Handler(source); await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Responses, options: AnnotationStreamFixtures.Options);
        var output = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, Request());
        Assert.Equal("length", output[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(output[^1].Metadata.Conversion!.UpstreamData.GetRawText())); Assert.Equal(1, handler.Calls);
    }

    /// <summary>只使用本地替代 HTTP 与最多三字节分片的 UTF-8 SSE；不存在真实模型或搜索调用。</summary>
    private static StubHttpHandler Handler(IEnumerable<JsonObject> source, bool checkRequest = false) => new(async (request, token) =>
    {
        if (checkRequest)
        {
            Assert.Equal("https://wire.invalid/v1/responses", request.RequestUri!.AbsoluteUri);
            CustomToolConversionTests.Equal(CustomToolFixtures.Parse("""{"model":"model-a","stream":true,"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"start"}]}]}"""),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(string.Concat(source.Select(e => "data: " + e.ToJsonString() + "\n\n")))) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    });
    /// <summary>最小原生用户请求。</summary>
    private static JsonObject Request() => CustomToolFixtures.Parse("""{"model":"public","messages":[{"role":"user","content":"start"}]}""");
}
