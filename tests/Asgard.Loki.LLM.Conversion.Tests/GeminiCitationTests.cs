using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证完整反向 URL 引用转换、Unicode 单位、真实 part 关联与严格损耗边界。</summary>
public sealed class GeminiCitationTests
{
    /// <summary>六份完整来源比较独立预期，来源重复和引用顺序不被去重或合并。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public void CompleteResponse_MatchesIndependentGolden(LlmProtocol sourceProtocol, LlmCitationIndexUnit unit)
    {
        var source = GeminiCitationFixtures.Read(sourceProtocol.ToString() + unit); var before = source.ToJsonString();
        var result = GeminiCitationFixtures.Convert(source, sourceProtocol, unit);
        CustomToolConversionTests.Equal(GeminiCitationFixtures.Read("Gemini" + sourceProtocol), CustomToolConversionTests.Read(result));
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>反向读取仍保留各块文本、URL、标题和范围；函数 ID 与参数语义保持关联。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public void Roundtrip_PreservesCitationsAndToolIdentity(LlmProtocol sourceProtocol, LlmCitationIndexUnit unit)
    {
        var source = GeminiCitationFixtures.Read(sourceProtocol.ToString() + unit);
        var gemini = CustomToolConversionTests.Read(GeminiCitationFixtures.Convert(source, sourceProtocol, unit));
        var replay = CustomToolConversionTests.Read(GeminiGroundingFixtures.Convert(gemini, sourceProtocol, GeminiGroundingFixtures.Options(unit)));
        var original = GeminiCitationFixtures.Citations(source, sourceProtocol).ToArray();
        var restored = GeminiCitationFixtures.Citations(replay, sourceProtocol).ToArray(); Assert.Equal(original.Length, restored.Length);
        for (var i = 0; i < original.Length; i++) CustomToolConversionTests.Equal(original[i], restored[i]);
        if (sourceProtocol == LlmProtocol.ChatCompletions)
        {
            Assert.Equal(source["choices"]![0]!["message"]!["content"]!.GetValue<string>(), replay["choices"]![0]!["message"]!["content"]!.GetValue<string>());
            var call = replay["choices"]![0]!["message"]!["tool_calls"]![0]!;
            Assert.Equal("call_exact", call["id"]!.GetValue<string>()); Assert.Equal("lookup", call["function"]!["name"]!.GetValue<string>());
            CustomToolConversionTests.Equal(CustomToolFixtures.Parse("""{"q":"😀","n":0}"""), JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>()));
        }
        else
        {
            foreach (var i in new[] { 0, 2 }) CustomToolConversionTests.Equal(source["output"]![i]!["content"], replay["output"]![i]!["content"]);
            Assert.Equal("call_exact", replay["output"]![1]!["call_id"]!.GetValue<string>());
            Assert.Equal("lookup", replay["output"]![1]!["name"]!.GetValue<string>());
            CustomToolConversionTests.Equal(CustomToolFixtures.Parse("""{"q":"😀","n":0}"""), JsonNode.Parse(replay["output"]![1]!["arguments"]!.GetValue<string>()));
        }
        CustomToolConversionTests.Equal(source["usage"], replay["usage"]);
    }

    /// <summary>未声明单位时保持旧的严格边界，损耗许可也不会推测范围。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void UnspecifiedUnit_RemainsExplicitBoundary(LlmProtocol sourceProtocol)
    {
        var source = GeminiCitationFixtures.Read(sourceProtocol + "UnicodeScalar");
        var error = Assert.Throws<LlmConversionException>(() => GeminiCitationFixtures.Convert(source, sourceProtocol, LlmCitationIndexUnit.Unspecified));
        Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable");
        var result = GeminiCitationFixtures.Convert(source, sourceProtocol, LlmCitationIndexUnit.Unspecified, true);
        Assert.False(CustomToolConversionTests.Read(result)["candidates"]![0]!.AsObject().ContainsKey("groundingMetadata"));
        Assert.Contains(result.Diagnostics, d => d.Code == "search_citations_not_representable");
    }

    /// <summary>Unicode 字节或代码单元被截断、越界、倒置及非整数索引不能被宽松策略修复。</summary>
    [Theory] [InlineData("utf16")] [InlineData("utf8")] [InlineData("past-end")] [InlineData("reversed")]
    [InlineData("null")] [InlineData("fraction")] [InlineData("negative")] [InlineData("missing")]
    public void InvalidBoundary_IsHardFailure(string kind)
    {
        foreach (var protocol in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses })
            foreach (var allowLoss in new[] { false, true })
            {
                var unit = kind == "utf16" ? LlmCitationIndexUnit.Utf16CodeUnit : LlmCitationIndexUnit.Utf8Byte;
                var source = GeminiCitationFixtures.Read(protocol.ToString() + unit); var citation = GeminiCitationFixtures.Citations(source, protocol).First();
                switch (kind)
                {
                    case "utf16": citation["end_index"] = 2; break;
                    case "utf8": citation["end_index"] = 4; break;
                    case "past-end": citation["end_index"] = 999; break;
                    case "reversed": citation["start_index"] = 7; citation["end_index"] = 0; break;
                    case "null": citation["start_index"] = null; break;
                    case "fraction": citation["end_index"] = 1.5; break;
                    case "negative": citation["start_index"] = -1; break;
                    case "missing": citation.Remove("end_index"); break;
                }
                Assert.Throws<InvalidDataException>(() => GeminiCitationFixtures.Convert(source, protocol, unit, allowLoss));
            }
    }

    /// <summary>零宽范围与相互重叠引用按原顺序保留，不归一化 Unicode 或吞掉空 segment。</summary>
    [Theory] [MemberData(nameof(GeminiGroundingFixtures.Directions), MemberType = typeof(GeminiGroundingFixtures))]
    public void EmptyRangeAndOverlap_KeepIndependentSupports(LlmProtocol sourceProtocol, LlmCitationIndexUnit unit)
    {
        var source = GeminiCitationFixtures.Read(sourceProtocol.ToString() + unit);
        GeminiCitationFixtures.Citations(source, sourceProtocol).First()["end_index"] = 0;
        var data = CustomToolConversionTests.Read(GeminiCitationFixtures.Convert(source, sourceProtocol, unit));
        var expected = GeminiCitationFixtures.Read("Gemini" + sourceProtocol); var first = GeminiGroundingFixtures.Metadata(expected)["groundingSupports"]![0]!["segment"]!;
        first["endIndex"] = 0; first["text"] = "";
        CustomToolConversionTests.Equal(expected, data);
    }

    /// <summary>非 HTTP(S) 的来源需要特定损耗许可，只省略相关引用且不重新索引剩余 part。</summary>
    [Theory] [InlineData("file:///private/a")] [InlineData("gs://bucket/a")] [InlineData("/relative")] [InlineData("")]
    public void UnrepresentableSource_RequiresConsent(string url)
    {
        foreach (var protocol in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses })
        {
            var source = GeminiCitationFixtures.Read(protocol + "UnicodeScalar"); GeminiCitationFixtures.Citations(source, protocol).First()["url"] = url;
            var error = Assert.Throws<LlmConversionException>(() => GeminiCitationFixtures.Convert(source, protocol, LlmCitationIndexUnit.UnicodeScalar));
            Assert.Equal("grounding_source_not_representable", Assert.Single(error.Diagnostics).Code);
            var result = GeminiCitationFixtures.Convert(source, protocol, LlmCitationIndexUnit.UnicodeScalar, true);
            Assert.Equal("grounding_source_not_representable", Assert.Single(result.Diagnostics).Code);
            var expected = GeminiCitationFixtures.Read("Gemini" + protocol); var metadata = GeminiGroundingFixtures.Metadata(expected);
            metadata["groundingChunks"]!.AsArray().RemoveAt(0); metadata["groundingSupports"]!.AsArray().RemoveAt(0);
            for (var i = 0; i < 4; i++) metadata["groundingSupports"]![i]!["groundingChunkIndices"]![0] = i;
            CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result));
            CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        }
    }

    /// <summary>未知 null 与 false 也报告字段损耗，原始上游 JSON 独立保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnknownCitationField_IsDiagnosed(bool nullValue)
    {
        var source = GeminiCitationFixtures.Read("ResponsesUnicodeScalar");
        GeminiCitationFixtures.Citations(source, LlmProtocol.Responses).First()["future"] = nullValue ? null : JsonValue.Create(false);
        Assert.Throws<LlmConversionException>(() => GeminiCitationFixtures.Convert(source, LlmProtocol.Responses, LlmCitationIndexUnit.UnicodeScalar));
        var result = GeminiCitationFixtures.Convert(source, LlmProtocol.Responses, LlmCitationIndexUnit.UnicodeScalar, true);
        Assert.EndsWith(".future", Assert.Single(result.Diagnostics).Path);
        CustomToolConversionTests.Equal(GeminiCitationFixtures.Read("GeminiResponses"), CustomToolConversionTests.Read(result));
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>无引用响应不增加空元数据，原生复制保留未经解释的字段。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void NoCitationsAndNativeCopy_PreserveContracts(LlmProtocol protocol)
    {
        var source = GeminiCitationFixtures.Read(protocol + "UnicodeScalar");
        var native = LlmProtocolConverter.ConvertResponse(source, protocol, protocol);
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(native)); Assert.Empty(native.Diagnostics);
        if (protocol == LlmProtocol.ChatCompletions) source["choices"]![0]!["message"]!["annotations"]!.AsArray().Clear();
        else foreach (var item in source["output"]!.AsArray().OfType<JsonObject>().Where(i => i["type"]!.GetValue<string>() == "message"))
                foreach (var part in item["content"]!.AsArray()) part!["annotations"]!.AsArray().Clear();
        var result = GeminiCitationFixtures.Convert(source, protocol, LlmCitationIndexUnit.UnicodeScalar);
        Assert.False(CustomToolConversionTests.Read(result)["candidates"]![0]!.AsObject().ContainsKey("groundingMetadata")); Assert.Empty(result.Diagnostics);
    }

    /// <summary>完整引用映射不启用流式目标引用，显式单位和损耗许可都不产生伪造 grounding delta。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CitationStream_StillHasExplicitBoundary(bool allowLoss)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini,
            AnnotationStreamFixtures.Options with { AllowLoss = allowLoss });
        if (!allowLoss)
        {
            var error = Assert.Throws<LlmConversionException>(() => { foreach (var e in AnnotationStreamFixtures.Read()) converter.Push(e); converter.Complete(); });
            Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable");
        }
        else
        {
            var events = new List<LlmConversionResult>();
            foreach (var e in AnnotationStreamFixtures.Read()) events.AddRange(converter.Push(e));
            events.AddRange(converter.Complete());
            Assert.NotEmpty(events); Assert.All(events, e => Assert.DoesNotContain("groundingMetadata", e.Data.GetRawText()));
            Assert.Contains(converter.Diagnostics, d => d.Code == "search_citations_not_representable");
        }
    }

    /// <summary>引用不能把输出上限或安全终态转换为普通 STOP，工具内容与原始 ID 仍然保留。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    public void IncompleteOrFilteredReply_KeepsTerminalState(LlmProtocol protocol, bool filtered)
    {
        var source = GeminiCitationFixtures.Read(protocol + "UnicodeScalar");
        if (protocol == LlmProtocol.ChatCompletions) source["choices"]![0]!["finish_reason"] = filtered ? "content_filter" : "length";
        else
        {
            source["status"] = "incomplete";
            source["incomplete_details"] = new JsonObject { ["reason"] = filtered ? "content_filter" : "max_output_tokens" };
            foreach (var item in source["output"]!.AsArray()) item!["status"] = "incomplete";
        }
        var result = GeminiCitationFixtures.Convert(source, protocol, LlmCitationIndexUnit.UnicodeScalar);
        var expected = GeminiCitationFixtures.Read("Gemini" + protocol); expected["candidates"]![0]!["finishReason"] = filtered ? "SAFETY" : "MAX_TOKENS";
        CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result)); Assert.Empty(result.Diagnostics);
    }
}
