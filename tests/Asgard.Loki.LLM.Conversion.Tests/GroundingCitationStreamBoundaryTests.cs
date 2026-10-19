using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式引用流只在已声明的布局/单位内映射；无效关联与资源上限不能由 AllowLoss 绕过。</summary>
public sealed class GroundingCitationStreamBoundaryTests
{
    /// <summary>默认仍拒绝 grounding 流；没有目标单位或非法枚举会在读取源之前失败。</summary>
    [Fact]
    public void Configuration_IsExplicitAndValidated()
    {
        Assert.Throws<ArgumentException>(() => new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses,
            new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses,
            new() { GeminiGroundingStreamMode = (GeminiGroundingStreamMode)99 }));
        var error = Assert.Throws<LlmConversionException>(() => GroundingCitationStreamFixtures.Run(GroundingCitationStreamFixtures.Source(),
            new() { OpenAiCitationIndexUnit = LlmCitationIndexUnit.UnicodeScalar }));
        Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
    }
    /// <summary>错误 part、UTF-8 标量边界/范围/原文、来源索引/URI 都是硬协议失败，而不是显示损耗。</summary>
    [Theory]
    [InlineData("part")] [InlineData("start-byte")] [InlineData("end-byte")] [InlineData("range")] [InlineData("text")]
    [InlineData("source")] [InlineData("uri")] [InlineData("oneof")] [InlineData("negative-index")] [InlineData("fraction")]
    public void InvalidAssociation_FailsWithOrWithoutLoss(string kind)
    {
        foreach (var allowLoss in new[] { false, true })
        {
            var source = GroundingCitationStreamFixtures.Source(); var metadata = GroundingCitationStreamFixtures.Metadata(source, 1);
            var support = metadata["groundingSupports"]![0]!.AsObject(); var segment = support["segment"]!.AsObject();
            switch (kind)
            {
                case "part": segment["partIndex"] = 1; break;
                case "start-byte": segment["startIndex"] = 1; break;
                case "end-byte": segment["endIndex"] = 5; break;
                case "range": segment["endIndex"] = 999; break;
                case "text": segment["text"] = "wrong occurrence"; break;
                case "source": support["groundingChunkIndices"]![0] = int.MaxValue; break;
                case "uri": metadata["groundingChunks"]![0]!["web"]!["uri"] = "not-an-absolute-uri"; break;
                case "oneof": metadata["groundingChunks"]![0]!["image"] = new JsonObject(); break;
                case "negative-index": support["groundingChunkIndices"]![0] = -1; break;
                case "fraction": segment["endIndex"] = 0.5; break;
            }
            Assert.Throws<InvalidDataException>(() => GroundingCitationStreamFixtures.Run(source, GroundingCitationStreamFixtures.Options() with { AllowLoss = allowLoss }));
        }
    }
    /// <summary>多 part、函数穿插或附加 rendered-part 关联不能被压成 part 零后发出看似正确的引用。</summary>
    [Theory] [InlineData("multiple")] [InlineData("tool-before")] [InlineData("tool-after")] [InlineData("rendered-part")]
    public void NonSingleTextLayout_IsHardBoundary(string kind)
    {
        foreach (var allowLoss in new[] { false, true })
        {
            var source = GroundingCitationStreamFixtures.Source();
            switch (kind)
            {
                case "multiple": source[0]["candidates"]![0]!["content"]!["parts"]!.AsArray().Add(new JsonObject { ["text"] = "second" }); break;
                case "tool-before": source.Insert(0, Tool()); break;
                case "tool-after": source.Insert(2, Tool()); break;
                case "rendered-part": GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["renderedParts"] = new JsonArray(0); break;
            }
            var error = Assert.Throws<LlmConversionException>(() => GroundingCitationStreamFixtures.Run(source, GroundingCitationStreamFixtures.Options() with { AllowLoss = allowLoss }));
            Assert.Contains(error.Diagnostics, d => d.Code == "grounding_stream_layout_not_representable");
        }
    }
    /// <summary>未知/非空置信度和查询显示设置仍明确诊断；显式 null/false 不是可忽略的省略。</summary>
    [Theory] [InlineData("root-null")] [InlineData("source-false")] [InlineData("confidence")] [InlineData("query")]
    public void UnsupportedFields_RequireExplicitLossConsent(string kind)
    {
        var source = GroundingCitationStreamFixtures.Source(); var metadata = GroundingCitationStreamFixtures.Metadata(source, 1);
        switch (kind)
        {
            case "root-null": metadata["future"] = null; break;
            case "source-false": metadata["groundingChunks"]![0]!["future"] = false; break;
            case "confidence": metadata["groundingSupports"]![0]!["confidenceScores"] = new JsonArray(0.9, 0.8); break;
            case "query": metadata["webSearchQueries"] = new JsonArray("fixture search"); break;
        }
        Assert.Throws<LlmConversionException>(() => GroundingCitationStreamFixtures.Run(source));
        var converted = GroundingCitationStreamFixtures.Run(source, GroundingCitationStreamFixtures.Options() with { AllowLoss = true });
        Assert.NotEmpty(converted[^1].Diagnostics); Assert.Equal(4, converted.Count(item => item.Data.GetProperty("type").GetString() == "response.output_text.annotation.added"));
    }
    /// <summary>原生空重复元数据没有附加语义，不应伪报非空置信度或替代 part 绑定。</summary>
    [Fact]
    public void EmptyKnownRepeatedMetadata_IsEquivalentToOmission()
    {
        var source = GroundingCitationStreamFixtures.Source(); var support = GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!;
        support["confidenceScores"] = new JsonArray(); support["renderedParts"] = new JsonArray();
        var actual = GroundingCitationStreamFixtures.Run(source); var expected = GroundingCitationStreamFixtures.Expected(LlmCitationIndexUnit.UnicodeScalar);
        for (var i = 0; i < actual.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i], actual[i].Data);
        Assert.All(actual, item => Assert.Empty(item.Diagnostics)); Assert.True(support.AsObject().ContainsKey("confidenceScores"));
    }
    /// <summary>一个支持可关联多个来源：输出引用自身也受数量/字节上限，不能只限制输入条目。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CitationExpansion_IsIndependentlyBounded(bool bytes)
    {
        var source = GroundingCitationStreamFixtures.Source();
        GroundingCitationStreamFixtures.Metadata(source, 0)["groundingChunks"]![0]!["web"]!["uri"] = "https://long.invalid/" + new string('x', 200);
        GroundingCitationStreamFixtures.Metadata(source, 1).Remove("groundingChunks");
        GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"] = new JsonArray(new JsonObject
        {
            ["segment"] = new JsonObject { ["partIndex"] = 0, ["startIndex"] = 0, ["endIndex"] = 7, ["text"] = "前😀" },
            ["groundingChunkIndices"] = new JsonArray(0, 0, 0, 0)
        });
        source[2]["candidates"]![0]!.AsObject().Remove("groundingMetadata"); source[4]["candidates"]![0]!["groundingMetadata"] = new JsonObject();
        var retained = source.SelectMany(frame => frame["candidates"] is JsonArray list
            ? list.OfType<JsonObject>().Where(candidate => candidate["groundingMetadata"] is JsonObject).SelectMany(candidate =>
                candidate["groundingMetadata"]!.AsObject().Where(p => p.Value is JsonArray).SelectMany(p => p.Value!.AsArray()))
            : []).Sum(item => Encoding.UTF8.GetByteCount(item!.ToJsonString()));
        var options = GroundingCitationStreamFixtures.Options() with { AllowLoss = true, MaxStreamAnnotations = bytes ? 20 : 3, MaxStreamAnnotationBytes = bytes ? retained : 1024 * 1024 };
        var error = Assert.Throws<LlmConversionException>(() => GroundingCitationStreamFixtures.Run(source, options));
        Assert.Contains(error.Diagnostics, d => d.Code == (bytes ? "stream_mapped_citation_byte_limit" : "stream_mapped_citation_count_limit"));
    }
    /// <summary>原生复制与未携带 grounding 的普通函数流不受此显式显示映射改变。</summary>
    [Fact]
    public void NativeCopiesAndUngroundedToolCalls_RemainSeparate()
    {
        var source = GroundingCitationStreamFixtures.Source();
        var native = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Gemini,
            new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart, MaxStreamAnnotations = 1, MaxStreamAnnotationBytes = 1 });
        foreach (var frame in source) GroundingCitationStreamFixtures.Equal(frame, Assert.Single(native.Push(frame)).Data);
        Assert.Empty(native.Complete());
        var tool = Tool(); tool["candidates"]![0]!["finishReason"] = "STOP";
        var calls = GroundingCitationStreamFixtures.Run([tool], GroundingCitationStreamFixtures.Options());
        Assert.Contains(calls, item => item.Data.GetProperty("type").GetString() == "response.function_call_arguments.done");
        Assert.All(calls, item => Assert.Empty(item.Diagnostics));
    }
    /// <summary>单文本声明仅映射 Gemini→Responses，不替其他目标杜撰引用增量或改变其严格诊断。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void Profile_DoesNotEnableOtherTargetCitationProtocols(LlmProtocol target)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, target, GroundingCitationStreamFixtures.Options());
        var error = Assert.Throws<LlmConversionException>(() => converter.Push(GroundingCitationStreamFixtures.Source()[0]));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "grounding_metadata_not_representable");
    }
    /// <summary>仅构造原生完整函数参数，不执行该函数或制造结果。</summary>
    private static JsonObject Tool() => CustomToolFixtures.Parse("""{"responseId":"cited","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"call-one","name":"lookup","args":{}}}]}}]}""");
}
