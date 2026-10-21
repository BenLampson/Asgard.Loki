using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>累计来源索引是原生数据完整性，不会因调用者允许显示引用损耗而放弃校验。</summary>
public sealed class GroundingStreamIntegrityTests
{
    /// <summary>全局索引包含此前/后续帧；重复 URL 不去重，完整有损 Chat 事件与手工预期相同。</summary>
    [Fact]
    public void AccumulatedSourceIndices_MatchIndependentLossyGolden()
    {
        var source = GroundingStreamFixtures.Source(); var before = source.Select(v => v.ToJsonString()).ToArray();
        var actual = GroundingStreamFixtures.Run(source); var expected = GroundingStreamFixtures.Read("Chat").AsArray();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(actual[i].Data.GetRawText()));
        Assert.Contains(actual[^1].Diagnostics, d => d.Code == "grounding_metadata_not_representable");
        CustomToolConversionTests.Equal(source[^1], JsonNode.Parse(actual[^1].OriginalData.GetRawText())); Assert.Equal(before, source.Select(v => v.ToJsonString()).ToArray());
    }
    /// <summary>结构错误与未解析来源引用必须硬失败，不因有损目标或 AllowLoss 改为成功。</summary>
    [Theory]
    [InlineData("metadata-null")] [InlineData("chunks-null")] [InlineData("chunks-object")] [InlineData("chunk-null")]
    [InlineData("support-null")] [InlineData("supports-object")] [InlineData("indices-null")] [InlineData("negative-index")]
    [InlineData("fraction-index")] [InlineData("string-index")] [InlineData("huge-index")] [InlineData("missing-source")]
    [InlineData("part-negative")] [InlineData("start-null")] [InlineData("reversed-range")] [InlineData("segment-null")]
    [InlineData("segment-text")] [InlineData("rendered-part")] [InlineData("oneof")] [InlineData("image-oneof")]
    public void InvalidSourceReferences_FailEvenWithLossConsent(string kind)
    {
        var source = GroundingStreamFixtures.Source(); var metadata = GroundingStreamFixtures.Metadata(source, 1); var support = metadata["groundingSupports"]![0]!.AsObject();
        switch (kind)
        {
            case "metadata-null": source[1]["candidates"]![0]!["groundingMetadata"] = null; break;
            case "chunks-null": metadata["groundingChunks"] = null; break;
            case "chunks-object": metadata["groundingChunks"] = new JsonObject(); break;
            case "chunk-null": metadata["groundingChunks"]![0] = null; break;
            case "support-null": metadata["groundingSupports"]![0] = null; break;
            case "supports-object": metadata["groundingSupports"] = new JsonObject(); break;
            case "indices-null": support["groundingChunkIndices"] = null; break;
            case "negative-index": support["groundingChunkIndices"]![0] = -1; break;
            case "fraction-index": support["groundingChunkIndices"]![0] = 0.5; break;
            case "string-index": support["groundingChunkIndices"]![0] = "0"; break;
            case "huge-index": support["groundingChunkIndices"]![0] = int.MaxValue; break;
            case "missing-source": GroundingStreamFixtures.Metadata(source, 3)["groundingChunks"] = new JsonArray(); break;
            case "part-negative": support["segment"]!["partIndex"] = -1; break;
            case "start-null": support["segment"]!["startIndex"] = null; break;
            case "reversed-range": support["segment"]!["startIndex"] = 4; break;
            case "segment-null": support["segment"] = null; break;
            case "segment-text": support["segment"]!["text"] = false; break;
            case "rendered-part": GroundingStreamFixtures.Metadata(source, 2)["groundingSupports"]![0]!["renderedParts"]![0] = -1; break;
            case "oneof": metadata["groundingChunks"]![0]!["maps"] = new JsonObject(); break;
            case "image-oneof": metadata["groundingChunks"]![0]!["image"] = new JsonObject(); break;
        }
        Assert.Throws<InvalidDataException>(() => GroundingStreamFixtures.Run(source));
    }
    /// <summary>同一份来源累计校验用于每个跨协议目标，不靠选择更宽松目标绕过。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void UnresolvedGlobalReference_FailsForEveryTarget(LlmProtocol target)
    {
        var source = GroundingStreamFixtures.Source(); GroundingStreamFixtures.Metadata(source, 3)["groundingChunks"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => GroundingStreamFixtures.Run(source, target: target));
    }
    /// <summary>正好达到来源+支持总额/字节上限可成功，不能重复计数此前来源或把未知字段剪掉。</summary>
    [Fact]
    public void ExactSharedLimits_RetainAllNativeEntries()
    {
        var source = GroundingStreamFixtures.Source(); var items = GroundingStreamFixtures.Items(source).ToArray();
        var bytes = items.Sum(item => Encoding.UTF8.GetByteCount(item.ToJsonString()));
        var output = GroundingStreamFixtures.Run(source, GroundingStreamFixtures.Options with { MaxStreamAnnotations = items.Length, MaxStreamAnnotationBytes = bytes });
        Assert.Equal("stop", output[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString()); Assert.Equal(6, items.Length);
    }
    /// <summary>跨帧来源/支持共用硬资源上限，不接受普通损耗许可来丢弃超限项。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExceededLimits_FailWithoutSuccessfulTerminal(bool bytes)
    {
        var source = GroundingStreamFixtures.Source(); var all = GroundingStreamFixtures.Items(source).ToArray();
        var options = GroundingStreamFixtures.Options with
        { MaxStreamAnnotations = bytes ? all.Length : all.Length - 1, MaxStreamAnnotationBytes = bytes ? all.Sum(v => Encoding.UTF8.GetByteCount(v.ToJsonString())) - 1 : 1024 * 1024 };
        var error = Assert.Throws<LlmConversionException>(() => GroundingStreamFixtures.Run(source, options));
        Assert.Contains(error.Diagnostics, d => d.Code == (bytes ? "stream_grounding_byte_limit" : "stream_grounding_count_limit"));
    }
    /// <summary>已经 Push 的来源/支持不借用调用者容器；修改旧原文不能重定向待验证的全局引用。</summary>
    [Fact]
    public void CallerMutation_DoesNotAlterRetainedReferenceState()
    {
        var source = GroundingStreamFixtures.Source(); var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.ChatCompletions, GroundingStreamFixtures.Options);
        foreach (var frame in source.Take(3)) converter.Push(frame);
        GroundingStreamFixtures.Metadata(source, 2)["groundingSupports"]![0]!["groundingChunkIndices"]![0] = int.MaxValue;
        foreach (var frame in source.Skip(3)) converter.Push(frame);
        Assert.Equal("stop", converter.Complete()[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
    /// <summary>严格流边界仍拒绝未映射引用；相同协议保留原文而不套用转换状态上限。</summary>
    [Fact]
    public void StrictMappingStillFails_NativeIdentityRemainsUnfiltered()
    {
        var source = GroundingStreamFixtures.Source();
        var error = Assert.Throws<LlmConversionException>(() => GroundingStreamFixtures.Run(source, GroundingStreamFixtures.Options with { AllowLoss = false }));
        Assert.Contains(error.Diagnostics, d => d.Code == "grounding_metadata_not_representable");
        var native = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Gemini, new() { MaxStreamAnnotations = 1, MaxStreamAnnotationBytes = 1 });
        foreach (var item in source) CustomToolConversionTests.Equal(item, JsonNode.Parse(Assert.Single(native.Push(item)).Data.GetRawText()));
        Assert.Empty(native.Complete());
    }
    /// <summary>候选完成后的纯 metadata 仍在同次调用内累计并解析原始全局引用。</summary>
    [Fact]
    public void MetadataAfterCandidateFinish_ResolvesWithoutReopeningText()
    {
        var result = GroundingStreamFixtures.Run(GroundingStreamFixtures.MetadataTail());
        var expected = GroundingStreamFixtures.Read("Chat").AsArray(); Assert.Equal(expected.Count, result.Count);
        for (var i = 0; i < expected.Count; i++) CustomToolConversionTests.Equal(expected[i], JsonNode.Parse(result[i].Data.GetRawText()));
    }
    /// <summary>有损显示许可不能使携带阻断/未知根数据的末尾候选伪装为安全元数据尾帧。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MetadataTailWithBlockingRoot_DoesNotFinishSuccessfully(bool metadata)
        => Assert.Throws<InvalidDataException>(() => GroundingStreamFixtures.Run(metadata ? GroundingStreamFixtures.BlockedMetadataTail() : GroundingStreamFixtures.BlockedUsageTail()));
}
