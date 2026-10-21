using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>编码、文件大小、原生外壳与控制字段不能通过损耗许可被猜测或修复。</summary>
public sealed class TextFileBoundaryTests
{
    /// <summary>两个 Base64 源均检查坏字节、过长编码、代理项、截断和超出 Unicode 上限。</summary>
    public static TheoryData<LlmProtocol, string> InvalidUtf8
    {
        get
        {
            TheoryData<LlmProtocol, string> data = [];
            foreach (var source in new[] { LlmProtocol.Responses, LlmProtocol.Gemini })
                foreach (var value in new[] { "/w==", "wIA=", "7aCA", "8J8=", "9JCAgA==" }) data.Add(source, value); return data;
        }
    }
    /// <summary>显式 UTF-8 不允许用替代字符掩盖坏文件，不调用供应商或修改原文。</summary>
    [Theory] [MemberData(nameof(InvalidUtf8))]
    public void InvalidBytes_AreNotReplaced(LlmProtocol source, string data)
    {
        var request = TextFileFixtures.Read(source); TextFileFixtures.Data(request, source, data); var before = request.ToJsonString();
        foreach (var allowLoss in new[] { false, true })
            Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertRequest(request, source, LlmProtocol.ClaudeMessages, TextFileFixtures.Options with { AllowLoss = allowLoss }));
        Assert.Equal(before, request.ToJsonString());
    }
    /// <summary>原生字符串也必须是完整 Unicode，不能先序列化为替代字符再宣称保真。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void InvalidClaudeUnicode_IsNotSerializedAway(int kind)
    {
        var text = kind switch { 0 => new string((char)0xD800, 1), 1 => new string((char)0xDC00, 1), _ => "a" + (char)0xD800 + "b" };
        var request = TextFileFixtures.Read(LlmProtocol.ClaudeMessages); TextFileFixtures.Data(request, LlmProtocol.ClaudeMessages, text);
        foreach (var target in new[] { LlmProtocol.Responses, LlmProtocol.Gemini })
            Assert.Throws<InvalidDataException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, target, TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Equal(text, TextFileFixtures.File(request, LlmProtocol.ClaudeMessages)["data"]!.GetValue<string>());
    }
    /// <summary>标准 Base64 语法不是编码探测，不修补 URL 变体或空文件。</summary>
    [Theory] [InlineData("")] [InlineData("bad!")] [InlineData("_-8=")]
    public void MalformedBase64_IsRejected(string data)
    {
        foreach (var source in new[] { LlmProtocol.Responses, LlmProtocol.Gemini })
        {
            var request = TextFileFixtures.Read(source); TextFileFixtures.Data(request, source, data);
            Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, source, LlmProtocol.ClaudeMessages, TextFileFixtures.Options with { AllowLoss = true }));
        }
    }
    /// <summary>原生数据不可缺省或显式为 null；源引用不能由编码许可变成文件正文。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void MissingNullAndReference_AreNotGuessed(LlmProtocol source)
    {
        foreach (var explicitNull in new[] { false, true })
        {
            var request = TextFileFixtures.Read(source); var file = TextFileFixtures.File(request, source); var key = source == LlmProtocol.Responses ? "file_data" : "data";
            if (explicitNull) file[key] = null; else file.Remove(key);
            Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, source, Other(source), TextFileFixtures.Options with { AllowLoss = true }));
        }
        var reference = InputFileBoundaryTests.Reference(source);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(reference, source, Other(source), TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "provider_file_reference");
    }
    /// <summary>每份文件按实际 UTF-8 字节计量，19 字节允许，18 字节硬失败；两份文件不合并。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void DecodedByteLimit_IsExactAndPerFile(LlmProtocol source)
    {
        var request = TextFileFixtures.Read(source); var target = Other(source);
        var exact = LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options with { MaxTextFileBytes = 19 });
        CustomToolConversionTests.Equal(TextFileFixtures.Read(target), CustomToolConversionTests.Read(exact));
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options with { MaxTextFileBytes = 18, AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "text_file_byte_limit");
        if (source != LlmProtocol.ClaudeMessages)
        {
            TextFileFixtures.Data(request, source, Convert.ToBase64String(Enumerable.Repeat((byte)255, 20).ToArray()));
            var oversized = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options with { MaxTextFileBytes = 19 }));
            Assert.Contains(oversized.Diagnostics, d => d.Code == "text_file_byte_limit");
        }
    }
    /// <summary>单份纯文本的编码与大小配置进入常规参数校验。</summary>
    [Fact]
    public void InvalidConfiguration_IsRejected()
    {
        var request = TextFileFixtures.Read(LlmProtocol.Responses);
        foreach (var options in new[] { TextFileFixtures.Options with { TextFileEncoding = (LlmTextFileEncoding)99 }, TextFileFixtures.Options with { MaxTextFileBytes = 0 }, TextFileFixtures.Options with { MaxTextFileBytes = -1 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, options));
    }
    /// <summary>嵌套未来字段的 null/false 保持原文，显式同意后仅省略字段，不丢掉文件。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void UnknownFields_StillDiagnoseLoss(LlmProtocol source)
    {
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(false) })
        {
            var request = TextFileFixtures.Read(source); TextFileFixtures.File(request, source)["future"] = value?.DeepClone(); var before = request.ToJsonString();
            Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, Other(source), TextFileFixtures.Options));
            var result = LlmProtocolConverter.ConvertRequest(request, source, Other(source), TextFileFixtures.Options with { AllowLoss = true });
            Assert.EndsWith(".future", Assert.Single(result.Diagnostics).Path); CustomToolConversionTests.Equal(TextFileFixtures.Read(Other(source)), CustomToolConversionTests.Read(result));
            Assert.Equal(before, request.ToJsonString()); CustomToolConversionTests.Equal(request, JsonNode.Parse(result.OriginalData.GetRawText()));
        }
    }
    /// <summary>文档标题、引用/缓存/上下文控制不是编码的一部分，不能因识别 text/plain 就静默消失。</summary>
    [Theory] [InlineData("title")] [InlineData("context")] [InlineData("citations")] [InlineData("cache_control")]
    public void ClaudeDocumentMetadata_RemainsDiagnosed(string key)
    {
        var request = TextFileFixtures.Read(LlmProtocol.ClaudeMessages); InputFileFixtures.Parts(request, LlmProtocol.ClaudeMessages)[1]![key] = null;
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, TextFileFixtures.Options));
        Assert.EndsWith("." + key, Assert.Single(error.Diagnostics).Path);
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, TextFileFixtures.Options with { AllowLoss = true });
        CustomToolConversionTests.Equal(TextFileFixtures.Read(LlmProtocol.Responses), CustomToolConversionTests.Read(result)); Assert.EndsWith("." + key, Assert.Single(result.Diagnostics).Path);
    }
    /// <summary>Claude 的 Base64 source 仅声明 PDF；纯文本必须使用原生 text 源，不伪造新外壳。</summary>
    [Fact]
    public void ClaudeBase64PlainText_IsNotInvented()
    {
        var request = TextFileFixtures.Read(LlmProtocol.ClaudeMessages); TextFileFixtures.File(request, LlmProtocol.ClaudeMessages)["type"] = "base64";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "unsupported_file_mime");
    }
    /// <summary>BOM 作为原始内容保留，不自动去除，也不给无 BOM 文件添加前导码。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void LeadingBom_IsPreserved(LlmProtocol source)
    {
        var request = TextFileFixtures.Read(source); TextFileFixtures.Data(request, source, source == LlmProtocol.ClaudeMessages ? "\uFEFFlead" : "77u/bGVhZA==");
        var target = Other(source); var expected = TextFileFixtures.Read(target);
        TextFileFixtures.Data(expected, target, target == LlmProtocol.ClaudeMessages ? "\uFEFFlead" : "77u/bGVhZA==");
        var result = LlmProtocolConverter.ConvertRequest(request, source, target, TextFileFixtures.Options);
        CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result)); Assert.Empty(result.Diagnostics);
    }
    /// <summary>输入文件仍只属于用户数据，生成文件或系统内容不能被挪到用户消息。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NonUserFiles_RemainHardBoundary(LlmProtocol source)
    {
        var request = TextFileFixtures.Read(source);
        var message = source == LlmProtocol.Responses ? request["input"]![0] : source == LlmProtocol.Gemini ? request["contents"]![0] : request["messages"]![0];
        message!["role"] = source == LlmProtocol.Gemini ? "model" : "assistant";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, source, Other(source), TextFileFixtures.Options with { AllowLoss = true }));
        Assert.Contains(error.Diagnostics, d => d.Code == "unsupported_file_role");
    }
    /// <summary>空 Claude 文档不是缺省正文，不靠编码选项修补。</summary>
    [Fact]
    public void EmptyClaudeDocument_IsRejected()
    {
        var request = TextFileFixtures.Read(LlmProtocol.ClaudeMessages); TextFileFixtures.Data(request, LlmProtocol.ClaudeMessages, "");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, TextFileFixtures.Options));
    }
    /// <summary>明确 UTF-8 不等于所有 MIME；charset 参数、JSON 和 Markdown 都不自动重标为 text/plain。</summary>
    [Fact]
    public void OtherMimeAndCharset_AreNotRelabeled()
    {
        foreach (var mime in new[] { "text/plain;charset=utf-8", "application/json", "text/markdown" })
        {
            var request = TextFileFixtures.Read(LlmProtocol.Responses); TextFileFixtures.File(request, LlmProtocol.Responses)["file_data"] = "data:" + mime + ";base64,YWJj";
            var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, TextFileFixtures.Options with { AllowLoss = true }));
            Assert.Contains(error.Diagnostics, d => d.Code == "unsupported_file_mime");
        }
    }
    /// <summary>为单源边界测试选定支持文件容器的另一个目标。</summary>
    private static LlmProtocol Other(LlmProtocol source) => source == LlmProtocol.Responses ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses;
}
