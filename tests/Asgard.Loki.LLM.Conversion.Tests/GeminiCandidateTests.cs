using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>完整候选转换须保留身份、候选隔离和共享用量，不能只取第一个结果。</summary>
public sealed class GeminiCandidateTests
{
    /// <summary>完整独立快照同时覆盖工具、终态、Unicode 引用与稀疏索引。</summary>
    [Theory]
    [InlineData("text-two")]
    [InlineData("tools-isolated")]
    [InlineData("citations-isolated")]
    [InlineData("single-nonzero")]
    [InlineData("empty-and-filtered")]
    public void Complete_MatchesIndependentGolden(string name)
    {
        var sample = GeminiCandidateFixtures.Case(name); var source = sample["source"]!.AsObject(); var before = source.ToJsonString();
        var result = GeminiCandidateFixtures.Convert(source);
        GeminiCandidateFixtures.Equal(sample["target"]!, result.Data);
        GeminiCandidateFixtures.Equal(source, result.OriginalData);
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>索引不合法或重复时普通字段损耗许可不能改成重新编号。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"7\"")]
    [InlineData("true")]
    [InlineData("7")]
    public void InvalidCandidateIndex_IsHardFailure(string value)
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        source["candidates"]![1]!["index"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => GeminiCandidateFixtures.Convert(source, true));
    }

    /// <summary>Proto3 省略索引为零，不能把两个省略值当作两个不同候选。</summary>
    [Fact]
    public void MissingIndex_UsesZeroAndRejectsDuplicateZero()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        source["candidates"]![0]!.AsObject().Remove("index");
        var result = GeminiCandidateFixtures.Convert(source);
        Assert.Equal(0, result.Data.GetProperty("choices")[0].GetProperty("index").GetInt32());
        source["candidates"]![1]!.AsObject().Remove("index");
        Assert.Throws<InvalidDataException>(() => GeminiCandidateFixtures.Convert(source, true));
    }

    /// <summary>单个候选的未知字段仍需显式许可，诊断定位其数组位置而非原生索引。</summary>
    [Fact]
    public void CandidateExtension_HasOriginalPositionDiagnostic()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        source["candidates"]![1]!["future"] = null;
        var error = Assert.Throws<LlmConversionException>(() => GeminiCandidateFixtures.Convert(source));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.candidates[1].future");
        var result = GeminiCandidateFixtures.Convert(source, true);
        Assert.Contains(result.Diagnostics, d => d.Path == "$.candidates[1].future");
        GeminiCandidateFixtures.Equal(source, result.OriginalData);
    }

    /// <summary>后续候选的原生错误不能被前一个候选的成功或工具状态掩盖。</summary>
    [Theory]
    [InlineData("finish")]
    [InlineData("opaque")]
    [InlineData("role")]
    [InlineData("duplicate-tool")]
    [InlineData("bad-arguments")]
    [InlineData("citation-source")]
    public void InvalidLaterCandidate_RejectsWholeReply(string kind)
    {
        var source = GeminiCandidateFixtures.Case(kind == "citation-source" ? "citations-isolated" : "tools-isolated")["source"]!.AsObject();
        var candidate = source["candidates"]![kind == "citation-source" ? 1 : 2]!.AsObject();
        if (kind == "finish") candidate["finishReason"] = "FUTURE";
        else if (kind == "role") candidate["content"]!["role"] = "user";
        else if (kind == "opaque") candidate["content"]!["parts"]![0]!["thoughtSignature"] = "opaque";
        else if (kind == "duplicate-tool") candidate["content"]!["parts"]!.AsArray().Add(candidate["content"]!["parts"]![0]!.DeepClone());
        else if (kind == "bad-arguments") candidate["content"]!["parts"]![0]!["functionCall"]!["args"] = new JsonArray();
        else candidate["groundingMetadata"]!["groundingSupports"]![0]!["groundingChunkIndices"] = new JsonArray(1);
        if (kind is "finish" or "opaque")
        {
            var error = Assert.Throws<LlmConversionException>(() => GeminiCandidateFixtures.Convert(source, true));
            Assert.Contains(error.Diagnostics, d => d.Path.StartsWith("$.candidates[2]", StringComparison.Ordinal));
        }
        else if (kind == "citation-source") Assert.Throws<InvalidDataException>(() => GeminiCandidateFixtures.Convert(source, true));
        else Assert.Throws<ArgumentException>(() => GeminiCandidateFixtures.Convert(source, true));
    }

    /// <summary>没有候选的反馈响应不能捏造一个成功结果。</summary>
    [Fact]
    public void EmptyCandidates_IsRejected()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject(); source["candidates"] = new JsonArray();
        Assert.Throws<LlmConversionException>(() => GeminiCandidateFixtures.Convert(source, true));
    }

    /// <summary>单消息目标和现有流状态机没有获得多候选支持，不应选取候选零继续。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Responses)]
    public void OtherTargets_KeepMultipleCandidateBoundary(LlmProtocol target)
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, target, new() { AllowLoss = true }));
    }

    /// <summary>原生同协议副本保持未知字段、原始索引和全部候选。</summary>
    [Fact]
    public void NativeCopy_IsUnfiltered()
    {
        var source = GeminiCandidateFixtures.Case("tools-isolated")["source"]!.AsObject(); source["candidates"]![2]!["future"] = false;
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        GeminiCandidateFixtures.Equal(source, result.Data); Assert.Empty(result.Diagnostics);
    }

    /// <summary>目标及原文各自持有快照，源候选后续修改不影响已经返回的结果。</summary>
    [Fact]
    public void Result_IsIndependentOfCallerMutation()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject(); var original = source.DeepClone();
        var result = GeminiCandidateFixtures.Convert(source); source["candidates"]!.AsArray().Clear();
        GeminiCandidateFixtures.Equal(original, result.OriginalData); Assert.Equal(2, result.Data.GetProperty("choices").GetArrayLength());
    }

    /// <summary>根级用量或扩展不能随候选数量翻倍，也不能只保留最后一个候选的诊断。</summary>
    [Fact]
    public void SharedMetadata_IsConvertedOnceAndAllCandidateLossesRemain()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        source["future"] = null; source["usageMetadata"]!["future"] = false;
        source["candidates"]![0]!["future"] = null; source["candidates"]![1]!["future"] = null;
        var result = GeminiCandidateFixtures.Convert(source, true);
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.Single(result.Diagnostics, d => d.Path == "$.future");
        Assert.Single(result.Diagnostics, d => d.Path == "$usage.future");
        Assert.Contains(result.Diagnostics, d => d.Path == "$.candidates[0].future");
        Assert.Contains(result.Diagnostics, d => d.Path == "$.candidates[1].future");
        Assert.Equal(19, result.Data.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>候选分别表示备选答案，相同工具 ID 不跨备选答案拼接或相互占用。</summary>
    [Fact]
    public void ToolAssociation_IsScopedToCandidate()
    {
        var source = GeminiCandidateFixtures.Case("tools-isolated")["source"]!.AsObject();
        source["candidates"]![2]!["content"]!["parts"]![0]!["functionCall"]!["id"] = "call-a";
        var result = GeminiCandidateFixtures.Convert(source); var choices = result.Data.GetProperty("choices");
        Assert.Equal("call-a", choices[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-a", choices[2].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("stop", choices[1].GetProperty("finish_reason").GetString());
        Assert.Equal("length", choices[2].GetProperty("finish_reason").GetString());
    }

    /// <summary>未声明单位时不会把不同候选的字节范围猜成 OpenAI 字符范围。</summary>
    [Fact]
    public void Citations_StillRequireDeclaredUnits()
    {
        var source = GeminiCandidateFixtures.Case("citations-isolated")["source"]!.AsObject();
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, LlmProtocol.ChatCompletions));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.candidates[1].groundingMetadata");
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "grounding_metadata_not_representable"));
        Assert.DoesNotContain("annotations", result.Data.GetRawText());
    }

    /// <summary>完整转换不扩展流状态机；多个源候选应在任何成功输出前拒绝。</summary>
    [Fact]
    public void MultiCandidateStream_RemainsExplicitlyUnsupported()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.ChatCompletions, GeminiCandidateFixtures.Options(true));
        Assert.Throws<NotSupportedException>(() => converter.Push(source));
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>源没有 ID 时只生成一个根级标识，不生成候选专属的伪上游身份。</summary>
    [Fact]
    public void MissingRootId_DoesNotChangeCandidateIdentity()
    {
        var source = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject(); source.Remove("responseId");
        var result = GeminiCandidateFixtures.Convert(source);
        Assert.Matches("^loki_[0-9a-f]{32}$", result.Data.GetProperty("id").GetString());
        Assert.Equal(7, result.Data.GetProperty("choices")[0].GetProperty("index").GetInt32());
        Assert.Equal(2, result.Data.GetProperty("choices")[1].GetProperty("index").GetInt32());
        Assert.False(result.OriginalData.TryGetProperty("responseId", out _));
    }

}
