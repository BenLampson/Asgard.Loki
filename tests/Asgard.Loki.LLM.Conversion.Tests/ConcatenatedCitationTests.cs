using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>明确顺序拼接的原文、Unicode 范围、状态边界及既有绑定兼容性。</summary>
public sealed class ConcatenatedCitationTests
{
    /// <summary>跨消息/part 的全部增量和最终引用匹配独立金样，保留重复来源与空范围。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void FullIndependentTranscript_MatchesExactTarget(LlmCitationIndexUnit unit)
    {
        var source = ConcatenatedCitationFixtures.Source(unit); var before = string.Concat(source.Select(item => item.ToJsonString())); var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, ConcatenatedCitationFixtures.Options(unit)); List<LlmConversionResult> seen = [];
        foreach (var item in source) { var delta = converter.Push(item); Assert.All(delta, value => Assert.False(value.Data.GetProperty("candidates")[0].TryGetProperty("groundingMetadata", out _))); seen.AddRange(delta); } Assert.Equal(7, seen.Count); seen.AddRange(converter.Complete()); var expected = ConcatenatedCitationFixtures.Read("Gemini").AsArray(); Assert.Equal(expected.Count, seen.Count); for (var i = 0; i < seen.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i]!, seen[i].Data); Assert.All(seen, item => Assert.Empty(item.Diagnostics)); Assert.Equal(before, string.Concat(source.Select(item => item.ToJsonString()))); GroundingCitationStreamFixtures.Equal(source[^1], seen[^1].OriginalData);
    }
    /// <summary>累计结果可由单文本反向约定消费，精确保留文本及全部引用语义，不宣称恢复原生 part 分组。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void ReverseSingleTextProfile_PreservesFlattenedCitationSemantics(LlmCitationIndexUnit unit)
    {
        var source = ConcatenatedCitationFixtures.Source(unit); var forward = ConcatenatedCitationFixtures.Run(source, ConcatenatedCitationFixtures.Options(unit)); var reverse = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, ResponseCitationStreamFixtures.Options(unit)); foreach (var item in forward) reverse.Push(JsonNode.Parse(item.Data.GetRawText())!.AsObject()); var restored = reverse.Complete()[^1].Data.GetProperty("response"); var part = restored.GetProperty("output")[0].GetProperty("content")[0]; Assert.Equal("前😀é / 前😀é", part.GetProperty("text").GetString()); Assert.Equal(4, part.GetProperty("annotations").GetArrayLength()); var ranges = unit == LlmCitationIndexUnit.UnicodeScalar ? new[] { (2, 4), (2, 4), (7, 7), (7, 11) } : unit == LlmCitationIndexUnit.Utf16CodeUnit ? new[] { (3, 5), (3, 5), (8, 8), (8, 13) } : new[] { (7, 10), (7, 10), (13, 13), (13, 23) }; for (var i = 0; i < ranges.Length; i++) { Assert.Equal(ranges[i].Item1, part.GetProperty("annotations")[i].GetProperty("start_index").GetInt32()); Assert.Equal(ranges[i].Item2, part.GetProperty("annotations")[i].GetProperty("end_index").GetInt32()); } GroundingCitationStreamFixtures.Equal(source[^1]["response"]!["usage"]!, restored.GetProperty("usage"));
    }
    /// <summary>已有默认/SingleTextPart 配置的精确 v25 绑定字节不因新增 opt-in 枚举值改变。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PreviousV25BindingBytes_ArePreserved(bool single)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var native = new OpenAiLlmProvider(new StubHttpClientFactory(handler)); var options = single ? ResponseCitationStreamFixtures.Options() : new LlmConversionOptions(); var provider = new ConvertingLlmProvider(native, LlmProtocol.Responses, "conversion", options); var expected = ConcatenatedCitationFixtures.Read("V25DefaultOptions").AsObject(); if (single) { expected["OpenAiCitationIndexUnit"] = 1; expected["GeminiGroundingStreamMode"] = 1; } Assert.Equal(typeof(ConvertingLlmProvider).AssemblyQualifiedName + ":conversion-v25:" + native.Id + ":" + native.BindingIdentity + ":Responses:" + expected.ToJsonString(), provider.BindingIdentity); Assert.Equal(0, (int)GeminiGroundingStreamMode.Disabled); Assert.Equal(1, (int)GeminiGroundingStreamMode.SingleTextPart); Assert.Equal(2, (int)GeminiGroundingStreamMode.ConcatenatedTextParts);
    }
    /// <summary>旧的单文本配置仍拒绝多 part，新模式必须明确声明单位并且只用于正确方向。</summary>
    [Fact]
    public void Concatenation_IsExplicitAndDirectional()
    {
        Assert.Throws<LlmConversionException>(() => ConcatenatedCitationFixtures.Run(ConcatenatedCitationFixtures.Source(), ResponseCitationStreamFixtures.Options())); Assert.Throws<ArgumentException>(() => new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.ConcatenatedTextParts })); Assert.Throws<ArgumentException>(() => new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, ConcatenatedCitationFixtures.Options()));
    }
    /// <summary>合法源交错文本若直接拼接会改变已输出原文，必须在引用/终态发布前硬失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void InterleavedText_CannotBeRelabeledAsSequential(bool allowLoss)
    {
        var source = ConcatenatedCitationFixtures.Source(); ConcatenatedCitationFixtures.Interleave(source); var error = Assert.Throws<LlmConversionException>(() => ConcatenatedCitationFixtures.Run(source, ConcatenatedCitationFixtures.Options() with { AllowLoss = allowLoss })); Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "grounding_stream_layout_not_representable");
    }
    /// <summary>工具混排仍是独立边界，不把工具所处的 part 索引吞进文本范围。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ToolInterleaving_IsStillHardBoundary(bool allowLoss)
    {
        var source = ResponseCitationStreamFixtures.Source(); ResponseCitationStreamFixtures.AddSecondItem(source, true); var error = Assert.Throws<LlmConversionException>(() => ConcatenatedCitationFixtures.Run(source, ConcatenatedCitationFixtures.Options() with { AllowLoss = allowLoss })); Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "grounding_stream_layout_not_representable");
    }
    /// <summary>拼接不绕过源注解或膨胀后目标来源/支持的独立资源配额。</summary>
    [Theory] [InlineData("source-count")] [InlineData("target-count")] [InlineData("source-bytes")] [InlineData("target-bytes")] [InlineData("exact")]
    public void Limits_RemainIndependentAfterRebasing(string field)
    {
        var source = ConcatenatedCitationFixtures.Source(); var rawBytes = source.Where(item => item["type"]!.GetValue<string>() == "response.output_text.annotation.added").Sum(item => Encoding.UTF8.GetByteCount(item["annotation"]!.ToJsonString())); var grounding = ConcatenatedCitationFixtures.Read("Gemini")[7]!["candidates"]![0]!["groundingMetadata"]!; var targetBytes = new[] { "groundingChunks", "groundingSupports" }.SelectMany(name => grounding[name]!.AsArray()).Sum(item => Encoding.UTF8.GetByteCount(item!.ToJsonString())); Assert.True(targetBytes > rawBytes); var options = ConcatenatedCitationFixtures.Options() with { AllowLoss = true, MaxStreamAnnotations = field == "source-count" ? 3 : field == "target-count" ? 7 : 8, MaxStreamAnnotationBytes = field == "source-bytes" ? 1 : field == "target-bytes" ? rawBytes : targetBytes }; if (field == "exact") Assert.Equal(8, ConcatenatedCitationFixtures.Run(source, options).Count); else { var error = Assert.Throws<LlmConversionException>(() => ConcatenatedCitationFixtures.Run(source, options)); Assert.Contains(error.Diagnostics, item => item.Code == (field == "source-count" ? "stream_annotation_count_limit" : field == "target-count" ? "stream_grounding_count_limit" : field == "source-bytes" ? "stream_annotation_byte_limit" : "stream_grounding_byte_limit")); }
    }
    /// <summary>新枚举不影响同协议未知 JSON 的完整复制，也不要求原生调用猜测单位。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.Gemini)]
    public void NativeCopies_RemainUntouched(LlmProtocol protocol)
    {
        var input = JsonNode.Parse("""{"type":"future","nil":null,"opaque":{"number":9007199254740993}}""")!.AsObject(); var converter = new LlmProtocolStreamConverter(protocol, protocol, new() { GeminiGroundingStreamMode = GeminiGroundingStreamMode.ConcatenatedTextParts }); var result = Assert.Single(converter.Push(input)); GroundingCitationStreamFixtures.Equal(input, result.Data); Assert.Empty(result.Diagnostics); Assert.Empty(converter.Complete());
    }
    /// <summary>空 part 提前开始不改变累计正文，只有非空文本返回前序块才构成不可拼接交错。</summary>
    [Fact]
    public void EarlyEmptyPartStart_PreservesSequentialTextAndGrounding()
    {
        var source = ConcatenatedCitationFixtures.Source(); ConcatenatedCitationFixtures.StartNextPartEarly(source); var result = ConcatenatedCitationFixtures.Run(source); Assert.Equal("前😀é / 前😀é", string.Concat(result.SelectMany(item => item.Data.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()).Select(part => part.GetProperty("text").GetString()))); GroundingCitationStreamFixtures.Equal(ConcatenatedCitationFixtures.Read("Gemini")[7]!, result[^1].Data); Assert.All(result, item => Assert.Empty(item.Diagnostics));
    }
    /// <summary>拼接前每个块的源范围必须在完整 Unicode 边界，所有原生副本相同也不能修补。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void InvalidUnicodeBoundary_IsHardFailureBeforeRebasing(LlmCitationIndexUnit unit)
    {
        foreach (var allowLoss in new[] { false, true }) { var source = ConcatenatedCitationFixtures.Source(unit); ConcatenatedCitationFixtures.ChangeCitation(source, true, citation => citation["start_index"] = unit == LlmCitationIndexUnit.Utf16CodeUnit ? 2 : 4); Assert.Throws<InvalidDataException>(() => ConcatenatedCitationFixtures.Run(source, ConcatenatedCitationFixtures.Options(unit) with { AllowLoss = allowLoss })); }
    }
    /// <summary>原生 part 键必须属于最终内容，文字相同时也不能偷偷换序或重编号。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NativePartIdentity_DoesNotDependOnTextEquality(bool reordered)
    {
        foreach (var allowLoss in new[] { false, true }) { var source = ConcatenatedCitationFixtures.Source(); if (reordered) ConcatenatedCitationFixtures.ReorderIdenticalParts(source); else foreach (var item in source.Where(item => item["output_index"]?.GetValue<int>() == 0 && item.ContainsKey("content_index"))) item["content_index"] = item["content_index"]!.GetValue<int>() + 4; var options = ConcatenatedCitationFixtures.Options() with { AllowLoss = allowLoss }; if (reordered) Assert.Contains(Assert.Throws<LlmConversionException>(() => ConcatenatedCitationFixtures.Run(source, options)).Diagnostics, diagnostic => diagnostic.Code == "grounding_stream_layout_not_representable"); else Assert.Throws<InvalidDataException>(() => ConcatenatedCitationFixtures.Run(source, options)); }
    }
}
