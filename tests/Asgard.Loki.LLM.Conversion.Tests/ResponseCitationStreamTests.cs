using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式 Responses→Gemini 引用流的完整样本、反向回归及源快照一致性。</summary>
public sealed class ResponseCitationStreamTests
{
    /// <summary>原文、全部事件、来源顺序和重复引用匹配手算金样，正常 Complete 前不出现引用或终态。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void IndependentGoldens_AreExactAndIncremental(LlmCitationIndexUnit unit)
    {
        var source = ResponseCitationStreamFixtures.Source(unit); var before = new JsonArray(source.Select(item => (JsonNode)item.DeepClone()).ToArray()).ToJsonString(); var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, ResponseCitationStreamFixtures.Options(unit)); List<LlmConversionResult> actual = [];
        foreach (var item in source) { var delta = converter.Push(item); Assert.All(delta, result => Assert.False(result.Data.GetProperty("candidates")[0].TryGetProperty("groundingMetadata", out _))); actual.AddRange(delta); }
        Assert.Equal(4, actual.Count); actual.AddRange(converter.Complete()); var expected = ResponseCitationStreamFixtures.Read("Gemini").AsArray(); Assert.Equal(expected.Count, actual.Count); for (var i = 0; i < actual.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i]!, actual[i].Data); Assert.All(actual, result => Assert.Empty(result.Diagnostics)); Assert.Equal(before, new JsonArray(source.Select(item => (JsonNode)item.DeepClone()).ToArray()).ToJsonString());
    }
    /// <summary>目标约定可被已有反向映射器消费，最终引用/原文/用量与独立源响应相同。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void ReverseProfile_RestoresCompleteCitationsAndText(LlmCitationIndexUnit unit)
    {
        var source = ResponseCitationStreamFixtures.Source(unit); var forward = ResponseCitationStreamFixtures.Run(source, ResponseCitationStreamFixtures.Options(unit)); var reverse = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, ResponseCitationStreamFixtures.Options(unit)); foreach (var item in forward) reverse.Push(JsonNode.Parse(item.Data.GetRawText())!.AsObject()); var restored = reverse.Complete()[^1].Data.GetProperty("response"); GroundingCitationStreamFixtures.Equal(source[^1]["response"]!["output"]!, restored.GetProperty("output")); GroundingCitationStreamFixtures.Equal(source[^1]["response"]!["usage"]!, restored.GetProperty("usage"));
    }
    /// <summary>注解可从开始/part/item/最终快照首次提供，不必伪造 annotation.added 事件。</summary>
    [Theory] [InlineData("initial")] [InlineData("part")] [InlineData("item")] [InlineData("final")] [InlineData("late")]
    public void FirstCompleteSnapshot_CanSupplyCitations(string at)
    {
        var source = ResponseCitationStreamFixtures.Source(); if (at == "initial") source[3]["part"]!["annotations"] = source[12]["part"]!["annotations"]!.DeepClone();
        if (at == "late") { var done = source[11]; source.RemoveAt(11); source.Insert(7, done); } else source.RemoveRange(7, 4);
        if (at is "item" or "final") source.RemoveAll(item => item["type"]!.GetValue<string>() is "response.output_text.done" or "response.content_part.done"); if (at == "final") source.RemoveAll(item => item["type"]!.GetValue<string>() == "response.output_item.done"); for (var i = 0; i < source.Count; i++) source[i]["sequence_number"] = i; var result = ResponseCitationStreamFixtures.Run(source); GroundingCitationStreamFixtures.Equal(ResponseCitationStreamFixtures.Read("Gemini")[4]!, result[^1].Data);
    }
    /// <summary>原生索引/身份/快照与终态错误即使允许损耗也不能发布引用或成功完成。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("missing-id")] [InlineData("foreign-id")] [InlineData("negative-index")] [InlineData("sparse-index")] [InlineData("part-snapshot")] [InlineData("item-snapshot")] [InlineData("final-snapshot")] [InlineData("late-annotation")] [InlineData("range")] [InlineData("fraction")] [InlineData("text")] [InlineData("missing-terminal")] [InlineData("late-content")]
    public void InvalidSource_IsHardFailure(string kind)
    {
        foreach (var allowLoss in new[] { false, true })
        {
            var source = ResponseCitationStreamFixtures.Source();
            switch (kind)
            {
                case "duplicate": source.Insert(8, source[7].DeepClone().AsObject()); break;
                case "missing-id": source[7].Remove("item_id"); break;
                case "foreign-id": source[7]["item_id"] = "other"; break;
                case "negative-index": source[7]["annotation_index"] = -1; break;
                case "sparse-index": source[7]["annotation_index"] = int.MaxValue; break;
                case "part-snapshot": source[12]["part"]!["annotations"]![0]!["title"] = "changed"; break;
                case "item-snapshot": source[13]["item"]!["content"]![0]!["annotations"]![0]!["title"] = "changed"; break;
                case "final-snapshot": source[14]["response"]!["output"]![0]!["content"]![0]!["annotations"]![0]!["title"] = "changed"; break;
                case "late-annotation": source.Insert(13, source[7].DeepClone().AsObject()); break;
                case "range": ResponseCitationStreamFixtures.ChangeFirstAnnotation(source, annotation => annotation["end_index"] = 999); break;
                case "fraction": ResponseCitationStreamFixtures.ChangeFirstAnnotation(source, annotation => annotation["end_index"] = 0.5); break;
                case "text": source[11]["text"] = "wrong"; break;
                case "missing-terminal": source.RemoveAt(14); break;
                case "late-content": source.Add(source[4].DeepClone().AsObject()); break;
            }
            for (var i = 0; i < source.Count; i++) source[i]["sequence_number"] = i;
            var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, ResponseCitationStreamFixtures.Options() with { AllowLoss = allowLoss }); List<LlmConversionResult> seen = []; Assert.Throws<InvalidDataException>(() => { foreach (var item in source) seen.AddRange(converter.Push(item)); seen.AddRange(converter.Complete()); }); Assert.All(seen, result => Assert.False(result.Data.GetProperty("candidates")[0].TryGetProperty("groundingMetadata", out _)));
        }
    }
    /// <summary>不把多文本或工具混排硬改为一个引用 part，明确损耗许可也不改变布局关联。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MultipleBlocks_AreExplicitBoundary(bool tool)
    {
        foreach (var allowLoss in new[] { false, true }) { var source = ResponseCitationStreamFixtures.Source(); ResponseCitationStreamFixtures.AddSecondItem(source, tool); var error = Assert.Throws<LlmConversionException>(() => ResponseCitationStreamFixtures.Run(source, ResponseCitationStreamFixtures.Options() with { AllowLoss = allowLoss })); Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "grounding_stream_layout_not_representable"); }
    }
    /// <summary>源引用与膨胀后的来源/支持均受独立配额，不能允许损耗后截断。</summary>
    [Theory] [InlineData("input-count")] [InlineData("output-count")] [InlineData("input-bytes")] [InlineData("output-bytes")] [InlineData("exact")]
    public void SourceAndTargetLimits_AreIndependent(string field)
    {
        var expected = ResponseCitationStreamFixtures.Read("Gemini")[4]!["candidates"]![0]!["groundingMetadata"]!.AsObject(); var bytes = new[] { "groundingChunks", "groundingSupports" }.SelectMany(name => expected[name]!.AsArray()).Sum(item => Encoding.UTF8.GetByteCount(item!.ToJsonString())); var source = ResponseCitationStreamFixtures.Source(); var rawBytes = source[12]["part"]!["annotations"]!.AsArray().Sum(item => Encoding.UTF8.GetByteCount(item!.ToJsonString())); Assert.True(bytes > rawBytes);
        var options = ResponseCitationStreamFixtures.Options() with { AllowLoss = true, MaxStreamAnnotations = field == "input-count" ? 3 : field == "output-count" ? 7 : 8, MaxStreamAnnotationBytes = field == "input-bytes" ? 1 : field == "output-bytes" ? rawBytes : bytes };
        if (field == "exact") Assert.Equal(5, ResponseCitationStreamFixtures.Run(source, options).Count); else { var error = Assert.Throws<LlmConversionException>(() => ResponseCitationStreamFixtures.Run(source, options)); Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == (field == "input-count" ? "stream_annotation_count_limit" : field == "input-bytes" ? "stream_annotation_byte_limit" : field == "output-count" ? "stream_grounding_count_limit" : "stream_grounding_byte_limit")); }
    }
    /// <summary>默认不启用反向引用流，显式约定必须声明源端点单位。</summary>
    [Fact]
    public void ProfileAndUnit_MustBeExplicit()
    {
        Assert.Throws<ArgumentException>(() => new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart })); Assert.Throws<LlmConversionException>(() => ResponseCitationStreamFixtures.Run(ResponseCitationStreamFixtures.Source(), new() { OpenAiCitationIndexUnit = LlmCitationIndexUnit.UnicodeScalar }));
    }
    /// <summary>未知注解字段保留在原文并要求明确损耗许可，不被成功 URL 映射顺带隐藏。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnknownAnnotationFields_AreDiagnosed(bool nil)
    {
        var source = ResponseCitationStreamFixtures.Source(); ResponseCitationStreamFixtures.ChangeFirstAnnotation(source, annotation => annotation["future"] = nil ? null : JsonValue.Create(false)); Assert.Throws<LlmConversionException>(() => ResponseCitationStreamFixtures.Run(source)); var result = ResponseCitationStreamFixtures.Run(source, ResponseCitationStreamFixtures.Options() with { AllowLoss = true }); Assert.NotEmpty(result[^1].Diagnostics); Assert.True(result[^1].OriginalData.GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("annotations")[0].TryGetProperty("future", out _));
    }
    /// <summary>同协议仍完整复制未知事件和字段，单文本约定不会修改原生传递或猜测单位。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.Gemini)]
    public void NativeIdentityCopy_PreservesUnknownData(LlmProtocol protocol)
    {
        var source = JsonNode.Parse("""{"type":"future","error":{"opaque":false},"number":9007199254740993,"nil":null}""")!.AsObject(); var converter = new LlmProtocolStreamConverter(protocol, protocol, new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart }); var result = Assert.Single(converter.Push(source)); GroundingCitationStreamFixtures.Equal(source, result.Data); GroundingCitationStreamFixtures.Equal(source, result.OriginalData); Assert.Empty(result.Diagnostics); Assert.Empty(converter.Complete());
    }
    /// <summary>没有引用时不扩大单文本布局限制，普通工具调用保持原始 ID/名称/精确参数。</summary>
    [Fact]
    public void ToolWithoutCitations_KeepsExistingStreamBehavior()
    {
        var source = ResponseCitationStreamFixtures.Source(); foreach (var at in new[] { 12, 13, 14 }) { var annotations = at == 12 ? source[at]["part"]!["annotations"] : at == 13 ? source[at]["item"]!["content"]![0]!["annotations"] : source[at]["response"]!["output"]![0]!["content"]![0]!["annotations"]; annotations!.AsArray().Clear(); } source.RemoveRange(7, 4); ResponseCitationStreamFixtures.AddSecondItem(source, true); var result = ResponseCitationStreamFixtures.Run(source); var call = Assert.Single(result, item => item.Data.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray().Any(part => part.TryGetProperty("functionCall", out _))).Data.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("functionCall"); Assert.Equal("call-original", call.GetProperty("id").GetString()); Assert.Equal("lookup", call.GetProperty("name").GetString()); Assert.Equal(9007199254740993L, call.GetProperty("args").GetProperty("id").GetInt64()); Assert.All(result, item => Assert.Empty(item.Diagnostics));
    }
}
