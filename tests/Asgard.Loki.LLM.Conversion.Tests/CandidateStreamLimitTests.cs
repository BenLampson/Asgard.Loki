using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>多候选状态共用资源预算，显式损耗许可不能绕过上限。</summary>
public sealed class CandidateStreamLimitTests
{
    /// <summary>候选数按实际身份数量而非最大稀疏索引计数。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CandidateCount_UsesSharedBound(int maximum)
    {
        var sample = CandidateStreamFixtures.Case("gemini-mixed"); var source = CandidateStreamFixtures.Source(sample);
        var converter = CandidateStreamFixtures.Converter(sample, CandidateStreamFixtures.Options(true) with { MaxStreamCandidates = maximum });
        if (maximum == 1) AssertCode("stream_candidate_limit", () => converter.Push(source[0]));
        else { CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source); Assert.NotEmpty(converter.Complete()); }
    }

    /// <summary>累计输入边界的计量单位是完整源 JSON 序列化字节，覆盖所有候选及尾帧。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputBytes_ExactBoundAndOneByteShort(bool exact)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        var total = source.Sum(Size); var converter = CandidateStreamFixtures.Converter(sample,
            CandidateStreamFixtures.Options(true) with { MaxCandidateStreamInputBytes = total - (exact ? 0 : 1) });
        if (exact) { CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source); Assert.NotEmpty(converter.Complete()); }
        else AssertCode("candidate_stream_input_limit", () => CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source));
    }

    /// <summary>输出批次逐项计入完整源快照复制，不能只按目标小增量逃过元数据放大限制。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputBytes_CountOriginalSnapshotForEveryResult(bool exact)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        var targets = sample["target"]!.AsArray(); var ids = sample["sourceIndices"]!.AsArray();
        var batches = sample["pushCounts"]!.AsArray().Select(n => n!.GetValue<int>()).Append(targets.Count - sample["pushCounts"]!.AsArray().Sum(n => n!.GetValue<int>()));
        var start = 0; var maximum = 0;
        foreach (var count in batches)
        {
            var bytes = Enumerable.Range(start, count).Sum(i => Size(targets[i]) + Size(source[ids[i]!.GetValue<int>()]));
            maximum = Math.Max(maximum, bytes); start += count;
        }
        var converter = CandidateStreamFixtures.Converter(sample,
            CandidateStreamFixtures.Options(true) with { MaxCandidateStreamOutputBytes = maximum - (exact ? 0 : 1) });
        if (exact) { CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source); Assert.NotEmpty(converter.Complete()); }
        else AssertCode("candidate_stream_output_limit", () => { CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source); converter.Complete(); });
    }

    /// <summary>公共信封与各候选共享一个诊断数量上限。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Diagnostics_AreNotBudgetedPerCandidate(int maximum)
    {
        var sample = CandidateStreamFixtures.Case("chat-mixed"); var source = CandidateStreamFixtures.Source(sample);
        source[0]["future"] = null; source[0]["choices"]![0]!["delta"]!["future"] = false;
        var converter = CandidateStreamFixtures.Converter(sample,
            CandidateStreamFixtures.Options(true) with { MaxCandidateStreamDiagnostics = maximum });
        if (maximum == 1) AssertCode("candidate_stream_diagnostic_limit", () => converter.Push(source[0]));
        else { converter.Push(source[0]); Assert.Equal(2, converter.Diagnostics.Count); }
    }

    /// <summary>原生 grounding 完整性沿用既有条目上限，所有候选共用同一额度。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GroundingCount_IsSharedAcrossCandidates(int maximum)
    {
        var sample = CandidateStreamFixtures.Case("gemini-mixed"); var source = CandidateStreamFixtures.Source(sample);
        foreach (var candidate in source[0]["candidates"]!.AsArray()) candidate!["groundingMetadata"] = new JsonObject
        { ["groundingChunks"] = new JsonArray(new JsonObject { ["web"] = new JsonObject { ["uri"] = "https://a.invalid/", ["title"] = "same" } }) };
        var converter = CandidateStreamFixtures.Converter(sample,
            CandidateStreamFixtures.Options(true) with { MaxStreamAnnotations = maximum });
        if (maximum == 1) AssertCode("stream_annotation_count_limit", () => converter.Push(source[0]));
        else { CandidateStreamIntegrityTests.PushWithoutTerminal(converter, source); Assert.NotEmpty(converter.Complete()); }
    }

    /// <summary>所有公开资源边界都必须为正数，非法设置在状态创建时失败。</summary>
    [Theory]
    [InlineData("candidates", 0)]
    [InlineData("candidates", -1)]
    [InlineData("input", 0)]
    [InlineData("input", -1)]
    [InlineData("output", 0)]
    [InlineData("output", -1)]
    [InlineData("diagnostics", 0)]
    [InlineData("diagnostics", -1)]
    public void InvalidPolicy_IsRejectedAtConstruction(string field, int value)
    {
        var options = CandidateStreamFixtures.Options(); options = field switch
        { "candidates" => options with { MaxStreamCandidates = value }, "input" => options with { MaxCandidateStreamInputBytes = value },
            "output" => options with { MaxCandidateStreamOutputBytes = value }, _ => options with { MaxCandidateStreamDiagnostics = value } };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options));
    }

    /// <summary>默认新字段不进入旧配置序列化，显式开启或资源设置才改变现有绑定域。</summary>
    [Fact]
    public void DefaultPolicySerialization_IsBackwardCompatible()
    {
        var legacy = JsonSerializer.Serialize(new LlmConversionOptions());
        Assert.DoesNotContain("EnableMultipleCandidateStreams", legacy); Assert.DoesNotContain("MaxStreamCandidates", legacy);
        Assert.DoesNotContain("MaxCandidateStream", legacy);
        Assert.Contains("EnableMultipleCandidateStreams", JsonSerializer.Serialize(CandidateStreamFixtures.Options()));
    }

    /// <summary>声明的逻辑 JSON 字节计量，与 SSE 字节或 token 计费没有关系。</summary>
    private static int Size(JsonNode? node) => Encoding.UTF8.GetByteCount(node?.ToJsonString() ?? "null");
    /// <summary>只断言实际资源错误，不允许被其他未实现错误冒充通过。</summary>
    private static void AssertCode(string code, Action action)
    { var error = Assert.Throws<LlmConversionException>(action); Assert.Contains(error.Diagnostics, d => d.Code == code); }
}
