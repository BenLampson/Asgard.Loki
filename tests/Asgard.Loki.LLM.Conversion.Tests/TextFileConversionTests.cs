using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>纯文本文件保留容器、完整 UTF-8 内容、原始工具身份和显式损耗边界。</summary>
public sealed class TextFileConversionTests
{
    public static TheoryData<LlmProtocol, LlmProtocol> Directions => TextFileFixtures.Directions();
    /// <summary>完整独立请求和反向重放保留两个文件、emoji/组合字符、CRLF、NUL、BOM 及工具关联。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void Utf8Files_MatchFullGoldenAndReplay(LlmProtocol source, LlmProtocol target)
    {
        var request = TextFileFixtures.Read(source); var before = request.ToJsonString();
        var converted = LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options);
        var actual = CustomToolConversionTests.Read(converted); CustomToolConversionTests.Equal(TextFileFixtures.Read(target), actual); Assert.Empty(converted.Diagnostics);
        var replay = LlmProtocolConverter.ConvertRequest(actual, target, source, TextFileFixtures.Options);
        CustomToolConversionTests.Equal(request, CustomToolConversionTests.Read(replay)); Assert.Empty(replay.Diagnostics); Assert.Equal(before, request.ToJsonString());
        CustomToolConversionTests.Equal(request, JsonNode.Parse(converted.OriginalData.GetRawText()));
    }
    /// <summary>编码声明不能由 AllowLoss 替代，也不从文本或文件名猜测。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void UnspecifiedEncoding_IsHardBoundary(LlmProtocol source, LlmProtocol target)
    {
        foreach (var allowLoss in new[] { false, true })
        {
            var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(TextFileFixtures.Read(source), source, target, new() { AllowLoss = allowLoss }));
            Assert.Contains(error.Diagnostics, d => d.Code == "text_file_encoding_required");
        }
    }
    /// <summary>Chat 没有非 PDF 原生文件块；不能以损耗许可把文档拼成用户消息文本。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void ChatTarget_IsNotFabricated(LlmProtocol source)
    {
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(TextFileFixtures.Read(source), source, LlmProtocol.ChatCompletions,
            TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "text_file_target_not_representable");
    }
    /// <summary>不合法的 Chat 非 PDF 源也不能被接受，再宣称已保真转换。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void ChatNonPdfSource_IsRejected(LlmProtocol target)
    {
        var source = InputFileFixtures.Read(LlmProtocol.ChatCompletions);
        InputFileFixtures.File(source, LlmProtocol.ChatCompletions)["file_data"] = "data:text/plain;base64," + TextFileFixtures.Encoded;
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions, target, TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "unsupported_file_mime");
    }
    /// <summary>三个原生外壳不受转换编码/内存限制过滤，未来字段和不支持的文件仍完整复制。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NativeCopies_RemainUnfiltered(LlmProtocol protocol)
    {
        var request = TextFileFixtures.Read(protocol); TextFileFixtures.File(request, protocol)["future"] = null;
        var result = LlmProtocolConverter.ConvertRequest(request, protocol, protocol, new() { MaxTextFileBytes = 1 });
        CustomToolConversionTests.Equal(request, CustomToolConversionTests.Read(result)); Assert.Empty(result.Diagnostics);
    }
    /// <summary>文件名与文档标题不同义；许可后只遗漏名称并报告诊断，保留完整正文和原文。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void FilenameLoss_RemainsExplicit(LlmProtocol target)
    {
        var request = TextFileFixtures.Read(LlmProtocol.Responses); TextFileFixtures.File(request, LlmProtocol.Responses)["filename"] = "报告😀.txt";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, target, TextFileFixtures.Options));
        Assert.Equal("file_name_not_representable", Assert.Single(error.Diagnostics).Code);
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, target, TextFileFixtures.Options with { AllowLoss = true });
        CustomToolConversionTests.Equal(TextFileFixtures.Read(target), CustomToolConversionTests.Read(result)); Assert.Equal("file_name_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("报告😀.txt", result.OriginalData.GetProperty("input")[0].GetProperty("content")[1].GetProperty("filename").GetString());
    }
    /// <summary>支持文本文件不会放宽原生工具结果错误标志或改变关联种类。</summary>
    [Theory] [InlineData(LlmProtocol.Responses, LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.ClaudeMessages, LlmProtocol.Responses)]
    public void RichToolResult_PreservesFileAndOriginalId(LlmProtocol source, LlmProtocol target)
    {
        var request = TextFileFixtures.Read(source); var expected = TextFileFixtures.Read(target);
        Result(request, source)[source == LlmProtocol.Responses ? "output" : "content"] = new JsonArray(InputFileFixtures.Parts(request, source)[1]!.DeepClone());
        Result(expected, target)[target == LlmProtocol.Responses ? "output" : "content"] = new JsonArray(InputFileFixtures.Parts(expected, target)[1]!.DeepClone());
        var converted = LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options);
        CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(converted)); Assert.Empty(converted.Diagnostics);
        if (source == LlmProtocol.ClaudeMessages)
        {
            Result(request, source)["is_error"] = true;
            Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options with { AllowLoss = true }));
        }
    }
    /// <summary>既有完整历史中的原生工具结果块，避免重建或猜测关联 ID。</summary>
    private static JsonObject Result(JsonObject request, LlmProtocol protocol)
        => (protocol == LlmProtocol.Responses ? request["input"]![2] : request["messages"]![2]!["content"]![0])!.AsObject();
}
