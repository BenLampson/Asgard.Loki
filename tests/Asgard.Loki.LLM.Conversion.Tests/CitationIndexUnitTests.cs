using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证显式 OpenAI 单位的精确校验与多块 Unicode 前缀平移。</summary>
public sealed class CitationIndexUnitTests
{
    /// <summary>四个 Unicode 标量、五个 UTF-16 代码单元或十个 UTF-8 字节，不按字形合并组合字符。</summary>
    [Theory]
    [InlineData(LlmCitationIndexUnit.UnicodeScalar, 4)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit, 5)] [InlineData(LlmCitationIndexUnit.Utf8Byte, 10)]
    public void DeclaredUnit_RebasesUnicodePrefixExactly(LlmCitationIndexUnit unit, long prefixLength)
    {
        var source = UrlCitationFixtures.Read("Responses"); var content = source["output"]![0]!["content"]!.AsArray();
        content[0]!["text"] = "abc defgh";
        content.Insert(0, new JsonObject { ["type"] = "output_text", ["text"] = "前😀e\u0301", ["annotations"] = new JsonArray() });
        // ASCII 被引文本在所有单位下使用相同边界；只核对 Unicode 前缀的平移。
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, GeminiGroundingFixtures.Options(unit));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(prefixLength, UrlCitationFixtures.Citation(CustomToolConversionTests.Read(result), LlmProtocol.ChatCompletions)["start_index"]!.GetValue<long>());
    }

    /// <summary>声明后，范围必须是该单位下的真实边界，宽松损耗策略也不能修复错误索引。</summary>
    [Theory]
    [InlineData(LlmCitationIndexUnit.UnicodeScalar, 3)] [InlineData(LlmCitationIndexUnit.Utf16CodeUnit, 1)]
    [InlineData(LlmCitationIndexUnit.Utf16CodeUnit, 4)] [InlineData(LlmCitationIndexUnit.Utf8Byte, 1)]
    [InlineData(LlmCitationIndexUnit.Utf8Byte, 5)] [InlineData(LlmCitationIndexUnit.Utf8Byte, 8)]
    public void InvalidDeclaredBoundary_IsRejected(LlmCitationIndexUnit unit, int end)
    {
        var source = UrlCitationFixtures.Read("Responses"); source["output"]![0]!["content"]![0]!["text"] = "😀中";
        var annotations = UrlCitationFixtures.Annotations(source, LlmProtocol.Responses); annotations.RemoveAt(1);
        var citation = UrlCitationFixtures.Citation(source, LlmProtocol.Responses); citation["start_index"] = 0; citation["end_index"] = end;
        Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions,
            GeminiGroundingFixtures.Options(unit) with { AllowLoss = true }));
    }

    /// <summary>配置枚举在请求和响应转换前都验证，包括同协议复制入口。</summary>
    [Fact]
    public void InvalidUnit_IsRejectedBeforeConversion()
    {
        var options = new LlmConversionOptions { OpenAiCitationIndexUnit = (LlmCitationIndexUnit)99 };
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertResponse(new(), LlmProtocol.Gemini, LlmProtocol.Gemini, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(new(), LlmProtocol.Responses, LlmProtocol.Gemini, options));
    }
}
