using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>反向完整响应也逐候选保留关联，不把多个 Chat choices 合成一个 Gemini 结果。</summary>
public sealed class ChatGeminiCandidateTests
{
    /// <summary>完整独立目标覆盖原索引、空文本、工具、终态、引用和一次用量。</summary>
    [Theory]
    [InlineData("text-two")]
    [InlineData("tools-isolated")]
    [InlineData("citations-isolated")]
    [InlineData("single-nonzero")]
    [InlineData("empty-and-filtered")]
    public void Complete_MatchesIndependentGolden(string name)
    {
        var sample = GeminiCandidateFixtures.Read("chat-gemini-complete", name); var source = sample["source"]!.AsObject();
        var before = source.ToJsonString(); var result = Convert(source);
        GeminiCandidateFixtures.Equal(sample["target"]!, result.Data); GeminiCandidateFixtures.Equal(source, result.OriginalData);
        Assert.Equal(before, source.ToJsonString()); Assert.Empty(result.Diagnostics);
    }

    /// <summary>非法或重复 choice 索引不能通过损耗许可重新编号。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    [InlineData("7")]
    public void InvalidChoiceIndex_IsHardFailure(string value)
    {
        var source = Source("text-two"); source["choices"]![1]!["index"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Convert(source, true));
    }

    /// <summary>多个 Chat choices 必须有明确索引，不将缺失身份猜为零。</summary>
    [Fact]
    public void MissingIndex_InMultipleChoicesIsRejected()
    {
        var source = Source("text-two"); source["choices"]![1]!.AsObject().Remove("index");
        Assert.Throws<InvalidDataException>(() => Convert(source, true));
    }

    /// <summary>各候选的未知字段与文本诊断定位原始 choice 数组位置，根级诊断仅记一次。</summary>
    [Fact]
    public void Diagnostics_KeepChoicePositionsAndSharedMetadata()
    {
        var source = Source("text-two"); source["future"] = null;
        source["choices"]![0]!["future"] = false; source["choices"]![1]!["message"]!["future"] = null;
        var error = Assert.Throws<LlmConversionException>(() => Convert(source));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.choices[1].message.future");
        var result = Convert(source, true);
        Assert.Equal(3, result.Diagnostics.Count); Assert.Single(result.Diagnostics, d => d.Path == "$.future");
        Assert.Contains(result.Diagnostics, d => d.Path == "$.choices[0].future");
        Assert.Equal(19, result.Data.GetProperty("usageMetadata").GetProperty("totalTokenCount").GetInt32());
    }

    /// <summary>后续候选的未知终态不能变成普通成功结束。</summary>
    [Fact]
    public void InvalidLaterFinish_RejectsWholeReply()
    {
        var source = Source("text-two"); source["choices"]![1]!["finish_reason"] = "future";
        var error = Assert.Throws<LlmConversionException>(() => Convert(source, true));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.choices[1].finish_reason");
    }

    /// <summary>相同工具名称和相同备选工具 ID 都不能跨候选合并参数。</summary>
    [Fact]
    public void SameToolIdAcrossAlternatives_RemainsSeparate()
    {
        var source = Source("tools-isolated"); source["choices"]![2]!["message"]!["tool_calls"]![0]!["id"] = "call-a";
        var result = Convert(source); var candidates = result.Data.GetProperty("candidates");
        Assert.Equal("call-a", candidates[0].GetProperty("content").GetProperty("parts")[1].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("call-a", candidates[2].GetProperty("content").GetProperty("parts")[1].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("MAX_TOKENS", candidates[2].GetProperty("finishReason").GetString());
        Assert.False(candidates[1].GetProperty("content").GetProperty("parts")[0].TryGetProperty("functionCall", out _));
    }

    /// <summary>一个候选内部重复 ID 或非法参数仍为硬错误。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidToolInLaterChoice_IsRejected(bool duplicate)
    {
        var source = Source("tools-isolated"); var calls = source["choices"]![2]!["message"]!["tool_calls"]!.AsArray();
        if (duplicate) calls.Add(calls[0]!.DeepClone()); else calls[0]!["function"]!["arguments"] = "[]";
        if (duplicate) Assert.Throws<InvalidDataException>(() => Convert(source, true));
        else Assert.Throws<ArgumentException>(() => Convert(source, true));
    }

    /// <summary>空 choices 不生成正常候选，其他目标也不获得隐式多候选支持。</summary>
    [Fact]
    public void EmptyChoices_AndSingleMessageTargetsRemainExplicit()
    {
        var source = Source("text-two");
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, new() { AllowLoss = true }));
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses, new() { AllowLoss = true }));
        source["choices"] = new JsonArray(); Assert.Throws<LlmConversionException>(() => Convert(source, true));
    }

    /// <summary>完整候选改动不会放行现有多候选流式状态机。</summary>
    [Fact]
    public void MultiChoiceStream_IsStillRejectedBeforeSuccess()
    {
        var source = Source("text-two"); var choices = source["choices"]!.AsArray();
        foreach (var node in choices) { var choice = node!.AsObject(); choice["delta"] = choice["message"]!.DeepClone(); choice.Remove("message"); }
        source["object"] = "chat.completion.chunk";
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.Gemini, GeminiCandidateFixtures.Options(true));
        Assert.Throws<NotSupportedException>(() => converter.Push(source)); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>只使用已声明单位的完整转换入口。</summary>
    private static LlmConversionResult Convert(JsonObject source, bool allowLoss = false)
        => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, GeminiCandidateFixtures.Options(allowLoss));
    /// <summary>独立样本是每次变异测试的源副本。</summary>
    private static JsonObject Source(string name) => GeminiCandidateFixtures.Read("chat-gemini-complete", name)["source"]!.AsObject();
}
