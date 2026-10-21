using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>引用的字段和范围必须可验证；未支持的历史、流式及其他协议保持显式边界。</summary>
public sealed class UrlCitationBoundaryTests
{
    /// <summary>两种引用外壳使用相同的非法数值矩阵，包含缺失、null、溢出和不合法顺序。</summary>
    public static TheoryData<LlmProtocol, string, string> InvalidRanges
    {
        get
        {
            TheoryData<LlmProtocol, string, string> cases = [];
            foreach (var protocol in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses })
                foreach (var (start, end) in new[] { ("null", "4"), ("-1", "4"), ("1.5", "4"), ("\"0\"", "4"), ("true", "4"),
                    ("0", "null"), ("0", "-1"), ("0", "1.5"), ("0", "9223372036854775808"), ("2", "1"), ("0", "9999") })
                    cases.Add(protocol, start, end);
            return cases;
        }
    }

    /// <summary>即使允许普通损耗也不修补无效范围；输入报文保持不变。</summary>
    [Theory] [MemberData(nameof(InvalidRanges))]
    public void InvalidRange_IsNeverRepaired(LlmProtocol protocol, string start, string end)
    {
        var source = UrlCitationFixtures.Read(protocol.ToString()); var citation = UrlCitationFixtures.Citation(source, protocol);
        citation["start_index"] = JsonNode.Parse(start); citation["end_index"] = JsonNode.Parse(end); var before = source.ToJsonString();
        Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertResponse(source, protocol, UrlCitationFixtures.Other(protocol), new() { AllowLoss = true }));
        Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>必需来源/范围字段不能把缺失或 null 转成空字符串和零。</summary>
    [Theory] [InlineData("start_index")] [InlineData("end_index")] [InlineData("url")] [InlineData("title")]
    public void MissingRequiredFields_Fail(string field)
    {
        var source = UrlCitationFixtures.Read("Responses"); UrlCitationFixtures.Citation(source, LlmProtocol.Responses).Remove(field);
        if (field is "start_index" or "end_index")
            Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
        else Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
    }

    /// <summary>未知嵌套字段不会因为认识引用外壳而消失，必须诊断并保留完整源 JSON。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void UnknownCitationFields_RequireLossConsent(LlmProtocol protocol)
    {
        var source = UrlCitationFixtures.Read(protocol.ToString()); UrlCitationFixtures.Citation(source, protocol)["future"] = false;
        var target = UrlCitationFixtures.Other(protocol);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, protocol, target));
        Assert.Contains(error.Diagnostics, d => d.Path.EndsWith(".future", StringComparison.Ordinal));
        var result = LlmProtocolConverter.ConvertResponse(source, protocol, target, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Path.EndsWith(".future", StringComparison.Ordinal));
        Assert.Equal(2, UrlCitationFixtures.Annotations(CustomToolConversionTests.Read(result), target).Count);
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>Chat 的外层注解扩展也不能被内部引用的字段检查掩盖。</summary>
    [Fact]
    public void UnknownChatAnnotationWrapper_IsNotSilentlyDiscarded()
    {
        var source = UrlCitationFixtures.Read("ChatCompletions"); UrlCitationFixtures.Annotations(source, LlmProtocol.ChatCompletions)[0]!["future"] = null;
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses));
        Assert.EndsWith(".future", Assert.Single(error.Diagnostics).Path);
    }

    /// <summary>文件引用并非 URL 引用，不应猜测其身份或链接。</summary>
    [Fact]
    public void DifferentAnnotationKind_IsDiagnosedIndependently()
    {
        var source = UrlCitationFixtures.Read("Responses"); UrlCitationFixtures.Annotations(source, LlmProtocol.Responses).Add(
            CustomToolFixtures.Parse("""{"type":"file_citation","file_id":"provider-file","filename":"file.pdf","index":0}"""));
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.Equal("annotation_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(2, UrlCitationFixtures.Annotations(CustomToolConversionTests.Read(result), LlmProtocol.ChatCompletions).Count);
        Assert.DoesNotContain("provider-file", result.Data.GetRawText()); Assert.Contains("provider-file", result.OriginalData.GetRawText());
    }

    /// <summary>Chat 请求的 assistant 参数没有已验证的引用字段，不把响应映射自动扩展为历史映射。</summary>
    [Fact]
    public void ResponseCitationHistory_RemainsExplicitlyUnsupported()
    {
        var source = new JsonObject { ["model"] = "model-a", ["input"] = UrlCitationFixtures.Read("Responses")["output"]!.DeepClone() };
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions));
        Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable");
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        Assert.DoesNotContain("source.invalid", result.Data.GetRawText()); CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>同协议复制不校验或删改未来原生注解。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void NativeCopy_PreservesUnknownAndOpaqueAnnotations(LlmProtocol protocol)
    {
        var source = UrlCitationFixtures.Read(protocol.ToString()); UrlCitationFixtures.Annotations(source, protocol).Add(
            CustomToolFixtures.Parse("""{"type":"future","opaque":"keep","start_index":null}"""));
        var result = LlmProtocolConverter.ConvertResponse(source, protocol, protocol);
        Assert.Empty(result.Diagnostics); CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(result));
    }

    /// <summary>Responses 引用流没有已验证的 Chat/Claude/Gemini 对应结构，不伪造引用增量或成功终态。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void CitationStream_StrictlyFailsBeforeSuccessfulTerminal(LlmProtocol target)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, target); var output = new List<LlmConversionResult>();
        foreach (var e in UrlCitationFixtures.Stream().Take(5)) output.AddRange(converter.Push(e));
        var error = Assert.Throws<LlmConversionException>(() => converter.Push(UrlCitationFixtures.Stream()[5]));
        Assert.Contains(error.Diagnostics, d => d.Code == "search_citations_not_representable");
        Assert.NotEmpty(output); Assert.Throws<InvalidOperationException>(() => converter.Complete());
        Assert.DoesNotContain(output, e => e.Data.GetRawText().Contains("response.completed", StringComparison.Ordinal) || e.Data.GetRawText().Contains("message_stop", StringComparison.Ordinal));
    }

    /// <summary>显式损耗仍沿用原来的诊断流，完整响应新增支持不能导致 done 快照与聚合文本发生新冲突。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void CitationStream_ExplicitLossKeepsExistingContract(LlmProtocol target)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, target, new() { AllowLoss = true }); var output = new List<LlmConversionResult>();
        foreach (var e in UrlCitationFixtures.Stream()) output.AddRange(converter.Push(e));
        output.AddRange(converter.Complete());
        Assert.Contains(converter.Diagnostics, d => d.Code == "search_citations_not_representable");
        Assert.NotEmpty(output); Assert.All(output, e => Assert.DoesNotContain("source.invalid", e.Data.GetRawText()));
    }

    /// <summary>原生流拷贝保留引用事件及所有完成快照，不受转换子集限制。</summary>
    [Fact]
    public void NativeStream_PreservesAllCitationEvents()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Responses);
        foreach (var e in UrlCitationFixtures.Stream()) CustomToolConversionTests.Equal(e, CustomToolConversionTests.Read(Assert.Single(converter.Push(e))));
        Assert.Empty(converter.Complete());
    }
}
