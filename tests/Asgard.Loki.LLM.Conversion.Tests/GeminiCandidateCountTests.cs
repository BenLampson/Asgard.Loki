using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>只对源码证实的 Gemini candidateCount 到 Chat n 添加显式请求控制。</summary>
public sealed class GeminiCandidateCountTests
{
    /// <summary>完整快照证明计数控制不会覆盖推理、输出上限或结构化输出约束。</summary>
    [Theory]
    [InlineData("count-settings")]
    [InlineData("count-one")]
    public void Request_MatchesIndependentGolden(string name)
    {
        var sample = GeminiCandidateFixtures.Read("gemini-chat-requests", name); var source = sample["source"]!.AsObject();
        var result = Convert(source); GeminiCandidateFixtures.Equal(sample["target"]!, result.Data);
        GeminiCandidateFixtures.Equal(source, result.OriginalData); Assert.Empty(result.Diagnostics);
        Assert.Equal(LlmProtocol.Gemini, result.Context!.SourceProtocol); Assert.Equal(LlmProtocol.ChatCompletions, result.Context.TargetProtocol);
    }

    /// <summary>显式 null、字符串、布尔、小数或越界值不是有效原生候选数量。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    public void InvalidCount_IsRejectedEvenWithLossConsent(string value)
    {
        var source = Request(); source["generationConfig"]!["candidateCount"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Convert(source, true));
    }

    /// <summary>不猜模型上限、不截断数量；供应商是否接受仍由调用者确认。</summary>
    [Fact]
    public void ExplicitInt32Count_IsPreservedWithoutModelInference()
    {
        var source = Request(); source["generationConfig"]!["candidateCount"] = int.MaxValue;
        Assert.Equal(int.MaxValue, Convert(source).Data.GetProperty("n").GetInt32());
    }

    /// <summary>缺失数量保持缺失，配置与源对象不会因转换被修改。</summary>
    [Fact]
    public void MissingCount_DoesNotInjectDefaultOrMutateCaller()
    {
        var source = Request(); var original = source.DeepClone(); var result = Convert(source);
        GeminiCandidateFixtures.Equal(original, result.OriginalData); Assert.True(JsonNode.DeepEquals(original, source));
        source["generationConfig"]!.AsObject().Remove("candidateCount");
        Assert.False(Convert(source).Data.TryGetProperty("n", out _));
        Assert.Equal(2, result.Data.GetProperty("n").GetInt32());
    }

    /// <summary>多候选流式不属于本轮支持，必须在原生 HTTP 之前明确拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleStreamCount_IsHardFailure(bool allowLoss)
    {
        var source = Request(); source["stream"] = true;
        var error = Assert.Throws<LlmConversionException>(() => Convert(source, allowLoss));
        Assert.Contains(error.Diagnostics, d => d.Code == "multiple_candidate_stream_not_supported");
    }

    /// <summary>单候选仍可使用原有流式转换；显式 false 不会变成 true。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneCandidate_StreamFlagIsPreserved(bool stream)
    {
        var source = GeminiCandidateFixtures.Read("gemini-chat-requests", "count-one")["source"]!.AsObject(); source["stream"] = stream;
        var result = Convert(source); Assert.Equal(1, result.Data.GetProperty("n").GetInt32()); Assert.Equal(stream, result.Data.GetProperty("stream").GetBoolean());
    }

    /// <summary>其他目标与反向请求数量尚未确认交付，不因同名设置而被悄悄开启。</summary>
    [Fact]
    public void UnsupportedPairs_KeepTheirBoundary()
    {
        var source = Request();
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, Options()));
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, Options()));
        var chat = JsonNode.Parse("""{"model":"model-a","messages":[{"role":"user","content":"hello"}],"n":2}""")!.AsObject();
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(chat, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, Options()));
    }

    /// <summary>同协议原生扩展不会继承转换数量边界或字段过滤。</summary>
    [Fact]
    public void NativeCopy_DoesNotFilterCandidateControls()
    {
        var source = Request(); source["stream"] = true; source["generationConfig"]!["future"] = null;
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        GeminiCandidateFixtures.Equal(source, result.Data); Assert.Empty(result.Diagnostics);
    }

    /// <summary>消费候选数量不允许同时吞掉其余未知配置，即使未知值是 null。</summary>
    [Fact]
    public void UnknownConfiguration_RemainsDiagnosedAfterCountExtraction()
    {
        var source = Request(); source["generationConfig"]!["future"] = null;
        var error = Assert.Throws<LlmConversionException>(() => Convert(source));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.generationConfig.future");
        var result = Convert(source, true); Assert.Equal(2, result.Data.GetProperty("n").GetInt32());
        GeminiCandidateFixtures.Equal(source, result.OriginalData);
        Assert.Single(result.Diagnostics, d => d.Path == "$.generationConfig.future");
    }

    /// <summary>数量控制不能把无效 stream 值当作完整调用来绕开流式边界。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"true\"")]
    public void InvalidStreamFlag_WithExplicitCountIsRejected(string value)
    {
        var source = Request(); source["stream"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Convert(source, true));
    }

    /// <summary>完整显式配置与既有请求转换选项保持一致。</summary>
    internal static LlmConversionOptions Options(bool allowLoss = false) => GeminiCandidateFixtures.Options(allowLoss) with { DefaultSchemaName = "Answer" };
    /// <summary>每次使用已提交的独立请求样本。</summary>
    internal static JsonObject Request() => GeminiCandidateFixtures.Read("gemini-chat-requests", "count-settings")["source"]!.AsObject();
    /// <summary>被测方向只有 Gemini 到 Chat，不通过其他协议中转。</summary>
    private static LlmConversionResult Convert(JsonObject source, bool allowLoss = false)
        => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.ChatCompletions, Options(allowLoss));
}
