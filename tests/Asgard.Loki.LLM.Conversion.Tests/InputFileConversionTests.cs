using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>用户 PDF 的字节、文本顺序、MIME 和原始工具历史必须在四协议间保持正确。</summary>
public sealed class InputFileConversionTests
{
    /// <summary>十二个原生请求方向使用各自独立编写的完整目标。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol> Directions => InputFileFixtures.Directions();

    /// <summary>完整快照及往返历史均包含文件前后文本、原始调用 ID、参数和关联结果。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void InlinePdf_MatchesAuthoredRequestGolden(LlmProtocol sourceProtocol, LlmProtocol target)
    {
        var source = InputFileFixtures.Read(sourceProtocol); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target);
        var data = CustomToolConversionTests.Read(result); CustomToolConversionTests.Equal(InputFileFixtures.Read(target, true), data);
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString()); CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        var replay = LlmProtocolConverter.ConvertRequest(data, target, sourceProtocol); CustomToolConversionTests.Equal(InputFileFixtures.Read(sourceProtocol, true), CustomToolConversionTests.Read(replay));
        var file = InputFileFixtures.File(data, target); var encoded = file[target is LlmProtocol.ChatCompletions or LlmProtocol.Responses ? "file_data" : "data"]!.GetValue<string>();
        if (encoded.StartsWith("data:", StringComparison.Ordinal)) encoded = encoded[(encoded.IndexOf(',', StringComparison.Ordinal) + 1)..];
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2d, 0x31, 0x2e, 0x37, 0x0a, 0x00, 0xff, 0x41, 0x42, 0x0a }, Convert.FromBase64String(encoded));
    }

    /// <summary>OpenAI 两个原生文件外壳之间直接保留文件名，不改写目录分隔符或 Unicode。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.Responses)] [InlineData(LlmProtocol.Responses, LlmProtocol.ChatCompletions)]
    public void OpenAiFilename_IsPreserved(LlmProtocol sourceProtocol, LlmProtocol target)
    {
        var source = InputFileFixtures.Read(sourceProtocol); InputFileFixtures.File(source, sourceProtocol)["filename"] = "目录/报告😀.pdf";
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target);
        Assert.Empty(result.Diagnostics); Assert.Equal("目录/报告😀.pdf", InputFileFixtures.File(CustomToolConversionTests.Read(result), target)["filename"]!.GetValue<string>());
    }

    /// <summary>没有同义文件名字段的目标必须报告损耗，不把文件名伪装成文档标题或提示文本。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.Gemini)]
    [InlineData(LlmProtocol.Responses, LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Responses, LlmProtocol.Gemini)]
    public void FilenameLoss_RequiresSpecificConsent(LlmProtocol sourceProtocol, LlmProtocol target)
    {
        var source = InputFileFixtures.Read(sourceProtocol); InputFileFixtures.File(source, sourceProtocol)["filename"] = "named.pdf";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target));
        Assert.Equal("file_name_not_representable", Assert.Single(error.Diagnostics).Code);
        var result = LlmProtocolConverter.ConvertRequest(source, sourceProtocol, target, new() { AllowLoss = true });
        Assert.Equal("file_name_not_representable", Assert.Single(result.Diagnostics).Code); Assert.DoesNotContain("named.pdf", result.Data.GetRawText());
        CustomToolConversionTests.Equal(InputFileFixtures.Read(target, true), CustomToolConversionTests.Read(result));
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>Claude 原生 URL-PDF 可以传给 Responses 原生 URL 文件输入，不需要 SDK 下载或创建文件 ID。</summary>
    [Fact]
    public void ClaudePdfUrl_ToResponses_PreservesExactReference()
    {
        var source = InputFileFixtures.Read(LlmProtocol.ClaudeMessages); InputFileFixtures.Parts(source, LlmProtocol.ClaudeMessages)[1]!["source"] =
            CustomToolFixtures.Parse("""{"type":"url","url":"https://source.invalid/报告%20one.pdf?q=1&b=二#page"}""");
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses); var expected = InputFileFixtures.Read(LlmProtocol.Responses, true);
        InputFileFixtures.Parts(expected, LlmProtocol.Responses)[1] = CustomToolFixtures.Parse("""{"type":"input_file","file_url":"https://source.invalid/报告%20one.pdf?q=1&b=二#page"}""");
        CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result)); Assert.Empty(result.Diagnostics);
    }

    /// <summary>多份文件不被合并或按 MIME 去重，前后文本和二进制顺序保持独立。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void MultipleFiles_KeepDistinctBytesAndOrder(LlmProtocol target)
    {
        var source = InputFileFixtures.Read(LlmProtocol.Responses); InputFileFixtures.Parts(source, LlmProtocol.Responses).Insert(2,
            CustomToolFixtures.Parse("""{"type":"input_file","file_data":"data:application/pdf;base64,JVBERi0yLjAKAAEC/w=="}"""));
        var expected = InputFileFixtures.Read(target, true);
        InputFileFixtures.Parts(expected, target).Insert(2, CustomToolFixtures.Parse(target switch
        {
            LlmProtocol.ChatCompletions => """{"type":"file","file":{"file_data":"data:application/pdf;base64,JVBERi0yLjAKAAEC/w=="}}""",
            LlmProtocol.ClaudeMessages => """{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"JVBERi0yLjAKAAEC/w=="}}""",
            _ => """{"inlineData":{"mimeType":"application/pdf","data":"JVBERi0yLjAKAAEC/w=="}}"""
        }));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target);
        Assert.Empty(result.Diagnostics); CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result));
    }
}
