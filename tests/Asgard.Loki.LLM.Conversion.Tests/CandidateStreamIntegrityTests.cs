using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>源候选和工具身份不能被损耗许可、其他候选成功或用量尾帧掩盖。</summary>
public sealed class CandidateStreamIntegrityTests
{
    /// <summary>索引必须为明确非负 int32，帧内重复也不能合并或重新编号。</summary>
    [Theory]
    [InlineData("chat-mixed", "null")]
    [InlineData("chat-mixed", "-1")]
    [InlineData("chat-mixed", "1.5")]
    [InlineData("chat-mixed", "2147483648")]
    [InlineData("chat-mixed", "\"2\"")]
    [InlineData("chat-mixed", "7")]
    [InlineData("gemini-mixed", "null")]
    [InlineData("gemini-mixed", "-1")]
    [InlineData("gemini-mixed", "1.5")]
    [InlineData("gemini-mixed", "2147483648")]
    [InlineData("gemini-mixed", "true")]
    [InlineData("gemini-mixed", "19")]
    public void InvalidIndex_IsHardFailure(string name, string value)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var key = name.StartsWith("chat", StringComparison.Ordinal) ? "choices" : "candidates";
        source[0][key]![1]!["index"] = JsonNode.Parse(value);
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true));
        Assert.Throws<InvalidDataException>(() => converter.Push(source[0])); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>一个候选缺少终态时，其他候选也不得先交付成功终态。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    public void MissingLaterFinish_RejectsWholeCompletion(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        if (name == "chat-mixed") source[3]["choices"]![0]!.AsObject().Remove("finish_reason");
        else source[1]["candidates"]![0]!.AsObject().Remove("finishReason");
        var converter = CandidateStreamFixtures.Converter(sample); PushWithoutTerminal(converter, source);
        Assert.Throws<InvalidDataException>(() => converter.Complete()); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>不同候选的帧也必须共享原始响应 ID、模型和创建时间。</summary>
    [Theory]
    [InlineData("chat-mixed", "id")]
    [InlineData("chat-mixed", "model")]
    [InlineData("chat-mixed", "created")]
    [InlineData("gemini-mixed", "responseId")]
    [InlineData("gemini-mixed", "modelVersion")]
    public void ChangedGlobalIdentity_IsRejected(string name, string field)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        if (field == "created") source[1][field] = 18; else source[1][field] = "changed";
        var converter = CandidateStreamFixtures.Converter(sample); converter.Push(source[0]);
        Assert.Throws<InvalidDataException>(() => converter.Push(source[1])); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>重复终态或终态后的新内容不能被另一候选仍在进行所掩盖。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    public void RepeatedFinish_PoisonsStream(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var converter = CandidateStreamFixtures.Converter(sample); PushWithoutTerminal(converter, source);
        var repeated = name == "chat-mixed" ? source[3] : source[1];
        Assert.Throws<InvalidDataException>(() => converter.Push(repeated)); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>函数身份不可在分片间改变，完整参数也不能通过其他候选的成功而跳过验证。</summary>
    [Theory]
    [InlineData("id")]
    [InlineData("arguments")]
    public void InvalidToolFragments_AreRejected(string kind)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        var call = source[2]["choices"]![1]!["delta"]!["tool_calls"]![0]!.AsObject();
        if (kind == "id") call["id"] = "different"; else call["function"]!["arguments"] = "1";
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true));
        if (kind == "id")
        {
            converter.Push(source[0]); converter.Push(source[1]); Assert.Throws<InvalidDataException>(() => converter.Push(source[2]));
        }
        else { PushWithoutTerminal(converter, source); Assert.ThrowsAny<System.Text.Json.JsonException>(() => converter.Complete()); }
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>字段损耗定位逻辑候选，不把稀疏索引当数组下标；公共信封诊断只记录一次。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootAndCandidateLosses_RemainExplicit(bool allowLoss)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[0]["future"] = null; source[0]["choices"]![0]!["delta"]!["future"] = false;
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(allowLoss));
        if (!allowLoss) { Assert.Throws<LlmConversionException>(() => converter.Push(source[0])); return; }
        PushWithoutTerminal(converter, source); var result = converter.Complete(); Assert.NotEmpty(result);
        Assert.Equal(2, converter.Diagnostics.Count);
        Assert.Single(converter.Diagnostics, d => d.Path == "$event.future");
        Assert.Single(converter.Diagnostics, d => d.Path == "$candidate[7].event.delta.future");
    }

    /// <summary>晚到的原生错误或 Gemini 阻断信息在损耗许可下也不能转换为成功。</summary>
    [Theory]
    [InlineData("chat-mixed", false)]
    [InlineData("gemini-mixed", false)]
    [InlineData("gemini-mixed", true)]
    public void LateNativeFailure_CannotDeliverSuccess(string name, bool blocked)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true)); PushWithoutTerminal(converter, source);
        var failure = source[^1].DeepClone().AsObject();
        if (blocked) failure["promptFeedback"] = new JsonObject { ["blockReason"] = "SAFETY" };
        else failure["error"] = new JsonObject { ["message"] = "late" };
        Assert.Throws<LlmConversionException>(() => converter.Push(failure)); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>不成对的 UTF-16 字符不能被序列化替换后继续交付。</summary>
    [Fact]
    public void InvalidUnicode_IsHardFailure()
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[0]["choices"]![0]!["delta"]!["content"] = new string((char)0xd800, 1);
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true));
        Assert.Throws<EncoderFallbackException>(() => converter.Push(source[0])); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>最后原生快照和共享用量均独立持有，不受调用者事后改写影响。</summary>
    [Fact]
    public void CallerMutation_DoesNotChangeFinalUsageOrOriginalData()
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        var original = source[^1].DeepClone(); var converter = CandidateStreamFixtures.Converter(sample);
        var first = converter.Push(source[0]); var firstRaw = source[0].DeepClone();
        foreach (var item in source.Skip(1)) converter.Push(item);
        source[0]["choices"]!.AsArray().Clear(); source[^1]["usage"]!["total_tokens"] = 9000;
        var result = converter.Complete(); GeminiCandidateFixtures.Equal(firstRaw, first[0].OriginalData);
        GeminiCandidateFixtures.Equal(original, result[^1].OriginalData);
        Assert.Equal(18, result[^1].Data.GetProperty("usageMetadata").GetProperty("totalTokenCount").GetInt32());
    }

    /// <summary>跨分片 JSON 的重复属性在对象转写前被拒绝，避免不同上游选择不同值。</summary>
    [Fact]
    public void DuplicateToolArgumentKeys_AreRejectedBeforeAnyFinish()
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[2]["choices"]![1]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = "1,\"n\":2}";
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true));
        PushWithoutTerminal(converter, source); Assert.ThrowsAny<System.Text.Json.JsonException>(() => converter.Complete());
    }

    /// <summary>参数字符串中的转义 Unicode 也必须有效，不能转成对象后替换非法字符。</summary>
    [Fact]
    public void EscapedInvalidUnicodeInArguments_IsRejected()
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[2]["choices"]![1]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = "1,\"text\":\"\\ud800\"}";
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true));
        PushWithoutTerminal(converter, source); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>公共 usage 的不可表达明细仍需诊断，不能因分离候选而被顺手删掉。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedUsageDetailLoss_RemainsExplicitAtCompletion(bool allowLoss)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[^1]["usage"]!["prompt_tokens_details"]!["audio_tokens"] = 2;
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(allowLoss));
        PushWithoutTerminal(converter, source);
        if (!allowLoss)
        {
            var error = Assert.Throws<LlmConversionException>(() => converter.Complete());
            Assert.Contains(error.Diagnostics, d => d.Path == "$usage.inputDetails.audio_tokens");
        }
        else
        {
            var result = converter.Complete(); Assert.Single(converter.Diagnostics);
            Assert.Contains(result[^1].Diagnostics, d => d.Path == "$usage.inputDetails.audio_tokens");
            GeminiCandidateFixtures.Equal(source[^1], result[^1].OriginalData);
        }
    }

    /// <summary>上游没有根级 ID 时只为整条公开流生成一次 ID，不按候选各生成一个。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    public void MissingNativeId_UsesOneSyntheticStreamIdentity(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        var nativeId = name == "chat-mixed" ? "id" : "responseId";
        foreach (var item in source) item.Remove(nativeId);
        var converter = CandidateStreamFixtures.Converter(sample); List<LlmConversionResult> seen = [];
        foreach (var item in source) seen.AddRange(converter.Push(item)); seen.AddRange(converter.Complete());
        var targetId = name == "chat-mixed" ? "responseId" : "id";
        var id = Assert.Single(seen.Select(e => e.Data.GetProperty(targetId).GetString()).Distinct());
        Assert.Matches("^loki_[0-9a-f]{32}$", id);
        Assert.All(seen, e => Assert.False(e.OriginalData.TryGetProperty(nativeId, out _)));
    }

    /// <summary>没有原生计数时不生成零值用量尾帧，未知与明确零保持不同。</summary>
    [Theory]
    [InlineData("chat-mixed")]
    [InlineData("gemini-mixed")]
    public void MissingUsage_DoesNotInventZeroCounters(string name)
    {
        var sample = CandidateStreamFixtures.Case(name); var source = CandidateStreamFixtures.Source(sample);
        source[^1].Remove(name == "chat-mixed" ? "usage" : "usageMetadata");
        var converter = CandidateStreamFixtures.Converter(sample); PushWithoutTerminal(converter, source);
        var final = converter.Complete(); Assert.Contains(final, CandidateStreamFixtures.IsTerminal);
        Assert.All(final, e => Assert.False(e.Data.TryGetProperty(name == "chat-mixed" ? "usageMetadata" : "usage", out _)));
    }

    /// <summary>Gemini 非 optional 原生索引省略为零，并在后续片段保持同一关联。</summary>
    [Fact]
    public void OmittedGeminiIndex_UsesProtoDefaultWithoutChangingOriginal()
    {
        var sample = CandidateStreamFixtures.Case("gemini-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[0]["candidates"]![0]!.AsObject().Remove("index"); source[1]["candidates"]![1]!.AsObject().Remove("index");
        var converter = CandidateStreamFixtures.Converter(sample); List<LlmConversionResult> seen = [];
        foreach (var item in source) seen.AddRange(converter.Push(item)); seen.AddRange(converter.Complete());
        var indices = seen.SelectMany(e => e.Data.GetProperty("choices").EnumerateArray()).Select(c => c.GetProperty("index").GetInt32()).Distinct().Order().ToArray();
        Assert.Equal(new[] { 0, int.MaxValue }, indices); Assert.False(seen[0].OriginalData.GetProperty("candidates")[0].TryGetProperty("index", out _));
    }

    /// <summary>任何中间批次均不得提前释放一个候选的终态或共享用量。</summary>
    internal static void PushWithoutTerminal(LlmProtocolStreamConverter converter, IEnumerable<JsonObject> source)
    {
        foreach (var item in source)
            Assert.All(converter.Push(item), e => Assert.False(CandidateStreamFixtures.IsTerminal(e)));
    }
}
