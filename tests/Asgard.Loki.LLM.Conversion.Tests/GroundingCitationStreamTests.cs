using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式 Gemini 单文本 grounding 到 Responses 原生注解事件/完成快照的保真转换。</summary>
public sealed class GroundingCitationStreamTests
{
    /// <summary>三个 Unicode 计量约定下，完整目标序列、原始关联和所有完成快照与独立预期一致。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void FullStream_MatchesNativeExpectedEvents(LlmCitationIndexUnit unit)
    {
        var source = GroundingCitationStreamFixtures.Source(); var before = source.Select(item => item.ToJsonString()).ToArray();
        var actual = GroundingCitationStreamFixtures.Run(source, GroundingCitationStreamFixtures.Options(unit)); var expected = GroundingCitationStreamFixtures.Expected(unit);
        Assert.Equal(expected.Count, actual.Count); Assert.Equal(15, actual.Count);
        for (var i = 0; i < expected.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i], actual[i].Data);
        Assert.All(actual, item => Assert.Empty(item.Diagnostics)); Assert.Equal(before, source.Select(item => item.ToJsonString()).ToArray());
        GroundingCitationStreamFixtures.Equal(source[^1], actual[^1].OriginalData);
    }
    /// <summary>文本即时公开，但全部引用与完成项必须等到候选 finish 之后的 metadata/用量正常结束。</summary>
    [Fact]
    public void TextIsIncremental_CitationsWaitForSourceCompletion()
    {
        var source = GroundingCitationStreamFixtures.Source();
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, GroundingCitationStreamFixtures.Options());
        var first = converter.Push(source[0]); Assert.Contains(first, item => item.Data.GetProperty("type").GetString() == "response.output_text.delta");
        foreach (var item in source.Skip(1))
            Assert.All(converter.Push(item), value => Assert.DoesNotContain(value.Data.GetProperty("type").GetString()!,
                new[] { "response.output_text.annotation.added", "response.completed", "response.incomplete" }));
        var end = converter.Complete(); Assert.Equal(4, end.Count(item => item.Data.GetProperty("type").GetString() == "response.output_text.annotation.added"));
        Assert.Equal("response.completed", end[^1].Data.GetProperty("type").GetString());
    }
    /// <summary>调用者随后改写已提交的源正文/来源/支持对象，不会改变保留的引用或目标完成快照。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void SubmittedSourceMutation_DoesNotChangeMappedCitations(LlmCitationIndexUnit unit)
    {
        var source = GroundingCitationStreamFixtures.Source();
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, GroundingCitationStreamFixtures.Options(unit));
        List<LlmConversionResult> actual = [];
        foreach (var frame in source.Take(3)) actual.AddRange(converter.Push(frame));
        source[0]["candidates"]![0]!["content"]!["parts"]![0]!["text"] = "mutated";
        GroundingCitationStreamFixtures.Metadata(source, 0)["groundingChunks"]![0]!["web"]!["uri"] = "https://changed.invalid/";
        GroundingCitationStreamFixtures.Metadata(source, 1)["groundingSupports"]![0]!["groundingChunkIndices"]![0] = int.MaxValue;
        GroundingCitationStreamFixtures.Metadata(source, 2)["groundingSupports"]![0]!["segment"]!["text"] = "changed";
        foreach (var frame in source.Skip(3)) actual.AddRange(converter.Push(frame)); actual.AddRange(converter.Complete());
        var expected = GroundingCitationStreamFixtures.Expected(unit); Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < actual.Count; i++) GroundingCitationStreamFixtures.Equal(expected[i], actual[i].Data);
    }
    /// <summary>生成的原生 Responses 流再经过独立源状态机校验，不只比对可视文本。</summary>
    [Theory] [InlineData(LlmCitationIndexUnit.UnicodeScalar)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit)] [InlineData(LlmCitationIndexUnit.Utf8Byte)]
    public void NativeTargetStream_PassesIndependentResponsesLifecycle(LlmCitationIndexUnit unit)
    {
        var target = GroundingCitationStreamFixtures.Run(GroundingCitationStreamFixtures.Source(), GroundingCitationStreamFixtures.Options(unit));
        var verifier = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions,
            new() { AllowLoss = true, OpenAiCitationIndexUnit = unit }); List<LlmConversionResult> chat = [];
        foreach (var item in target) chat.AddRange(verifier.Push(JsonNode.Parse(item.Data.GetRawText())!.AsObject())); chat.AddRange(verifier.Complete());
        var text = string.Concat(chat.Select(item => item.Data.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var value) ? value.GetString() : ""));
        Assert.Equal("前😀é / 前😀é", text); Assert.Equal("stop", chat[^1].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }
}
