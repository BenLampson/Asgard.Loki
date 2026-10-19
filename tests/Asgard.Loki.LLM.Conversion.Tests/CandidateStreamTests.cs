using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立多候选全流快照验证字段、工具关联、共享用量与延迟终态。</summary>
public sealed class CandidateStreamTests
{
    /// <summary>逐事件输出数量和完整目标序列必须同时吻合，不能只拼接文本比较。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    [InlineData("chat-parallel-tools")]
    [InlineData("chat-empty")]
    [InlineData("gemini-empty")]
    public void Stream_MatchesIndependentTranscript(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var before = source.Select(e => e.ToJsonString()).ToArray(); var converter = CandidateStreamFixtures.Converter(sample);
        List<LlmConversionResult> seen = [];
        for (var i = 0; i < source.Count; i++)
        {
            var delta = converter.Push(source[i]); Assert.Equal(sample["pushCounts"]![i]!.GetValue<int>(), delta.Count);
            Assert.All(delta, e => Assert.False(CandidateStreamFixtures.IsTerminal(e))); seen.AddRange(delta);
        }
        seen.AddRange(converter.Complete()); var expected = sample["target"]!.AsArray(); Assert.Equal(expected.Count, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            GeminiCandidateFixtures.Equal(expected[i]!, seen[i].Data);
            GeminiCandidateFixtures.Equal(source[sample["sourceIndices"]![i]!.GetValue<int>()], seen[i].OriginalData);
            Assert.Empty(seen[i].Diagnostics);
        }
        Assert.Equal(before, source.Select(e => e.ToJsonString()).ToArray()); Assert.Empty(converter.Diagnostics);
        Assert.Throws<InvalidOperationException>(() => converter.Push(source[0])); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>默认模式维持单候选边界，原生同协议副本不受新转换资源策略过滤。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    public void DefaultAndNativeIdentity_KeepTheirContracts(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var strict = CandidateStreamFixtures.Converter(sample, new()); Assert.Throws<NotSupportedException>(() => strict.Push(source[0]));
        var from = Enum.Parse<LlmProtocol>(sample["from"]!.GetValue<string>());
        var native = new LlmProtocolStreamConverter(from, from, CandidateStreamFixtures.Options() with { MaxStreamCandidates = 1, MaxCandidateStreamInputBytes = 1 });
        GeminiCandidateFixtures.Equal(source[0], Assert.Single(native.Push(source[0])).Data); Assert.Empty(native.Complete());
    }

    /// <summary>新增显式模式只属于 Chat/Gemini 一对，其他目标仍不能合并或挑选候选零。</summary>
    [Theory]
    [InlineData("chat-mixed", LlmProtocol.ClaudeMessages)]
    [InlineData("chat-mixed", LlmProtocol.Responses)]
    [InlineData("gemini-mixed", LlmProtocol.ClaudeMessages)]
    [InlineData("gemini-mixed", LlmProtocol.Responses)]
    public void OtherTargets_KeepSingleCandidateBoundary(string name, LlmProtocol target)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var converter = new LlmProtocolStreamConverter(Enum.Parse<LlmProtocol>(sample["from"]!.GetValue<string>()), target, CandidateStreamFixtures.Options(true));
        Assert.Throws<NotSupportedException>(() => converter.Push(source[0])); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

}
