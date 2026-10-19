using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>文件数据、归属、角色和未知控制必须明确处理，不能经损耗许可变成猜测的文件。</summary>
public sealed class InputFileBoundaryTests
{
    /// <summary>所有原生协议分别验证严格和普通损耗策略。</summary>
    public static TheoryData<LlmProtocol, bool> Policies
    {
        get { TheoryData<LlmProtocol, bool> data = []; foreach (var source in InputFileFixtures.Protocols) { data.Add(source, false); data.Add(source, true); } return data; }
    }

    /// <summary>四种文件 ID/URI 都不能根据相同模型名、协议或损耗许可推断为可跨目标使用。</summary>
    [Theory] [MemberData(nameof(Policies))]
    public void ProviderReference_IsNotPortable(LlmProtocol protocol, bool allowLoss)
    {
        var source = Reference(protocol); var before = source.ToJsonString();
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = allowLoss }));
        Assert.Equal("provider_file_reference", Assert.Single(error.Diagnostics).Code); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>同协议拷贝不删除原生文件引用、未来字段或 MIME 信息。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NativeCopy_PreservesProviderReference(LlmProtocol protocol)
    {
        var source = Reference(protocol); source["future"] = false;
        var result = LlmProtocolConverter.ConvertRequest(source, protocol, protocol); Assert.Empty(result.Diagnostics);
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(result));
    }

    /// <summary>即使显式允许元数据损耗，也不能忽略认识的文件内部未知字段。</summary>
    [Theory] [MemberData(nameof(Policies))]
    public void UnknownFileMetadata_IsDiagnosed(LlmProtocol protocol, bool explicitNull)
    {
        var source = InputFileFixtures.Read(protocol); InputFileFixtures.File(source, protocol)["future"] = explicitNull ? null : JsonValue.Create(false);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol)));
        Assert.EndsWith(".future", Assert.Single(error.Diagnostics).Path);
        var result = LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true });
        Assert.EndsWith(".future", Assert.Single(result.Diagnostics).Path); Assert.DoesNotContain("future", result.Data.GetRawText());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>标准 Base64 语法和非空数据由独立无效输入矩阵覆盖，不读取真实 PDF 文件。</summary>
    public static TheoryData<LlmProtocol, string> InvalidData
    {
        get { TheoryData<LlmProtocol, string> data = []; foreach (var source in InputFileFixtures.Protocols) foreach (var value in new[] { "", "bad!", "_-8=" }) data.Add(source, value); return data; }
    }

    /// <summary>不猜测 Base64URL、修补破损数据或发送一个空文件。</summary>
    [Theory] [MemberData(nameof(InvalidData))]
    public void InvalidBase64_FailsEvenWithLossConsent(LlmProtocol protocol, string value)
    {
        var source = InputFileFixtures.Read(protocol); var file = InputFileFixtures.File(source, protocol);
        file[protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses ? "file_data" : "data"] =
            protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses ? "data:application/pdf;base64," + value : value;
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true }));
    }

    /// <summary>缺失和显式 null 的文件数据都不变成空文件；四协议的字段名分别检查。</summary>
    [Theory] [MemberData(nameof(Policies))]
    public void MissingOrNullData_IsNotAnEmptyFile(LlmProtocol protocol, bool explicitNull)
    {
        var source = InputFileFixtures.Read(protocol); var file = InputFileFixtures.File(source, protocol);
        var field = protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses ? "file_data" : "data";
        if (explicitNull) file[field] = null; else file.Remove(field);
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true }));
    }

    /// <summary>文件名扩展不是 MIME 声明，裸 Base64 不会因为叫 pdf 而被猜成 PDF。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    public void Filename_DoesNotSupplyMissingMime(LlmProtocol protocol)
    {
        var source = InputFileFixtures.Read(protocol); var file = InputFileFixtures.File(source, protocol);
        file["file_data"] = "JVBERi0xLjcKAP9BQgo="; file["filename"] = "looks-like.pdf";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true }));
        Assert.Equal("file_mime_required", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>非 PDF 不重标为 PDF；Responses 纯文本缺少编码声明，Chat/Claude Base64 外壳不支持该类型。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void OtherMime_IsNeverRelabeledAsPdf(LlmProtocol protocol)
    {
        var source = InputFileFixtures.Read(protocol); var file = InputFileFixtures.File(source, protocol);
        if (protocol == LlmProtocol.ClaudeMessages) file["media_type"] = "text/plain";
        else file["file_data"] = "data:text/plain;base64,YWJj";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true }));
        Assert.Equal(protocol == LlmProtocol.Responses ? "text_file_encoding_required" : "unsupported_file_mime", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>来源冲突不能从多个引用中随意挑一个，null 引用也不隐藏为省略。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ConflictingSourceFields_AreNotSelectedHeuristically(bool explicitNull)
    {
        var source = InputFileFixtures.Read(LlmProtocol.Responses); InputFileFixtures.File(source, LlmProtocol.Responses)["file_id"] = explicitNull ? null : JsonValue.Create("file-id");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
    }

    /// <summary>不能把 assistant 文件移到 user，避免改变指令层级或把生成文件冒充输入文件。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NonUserFileRole_IsRejected(LlmProtocol protocol)
    {
        var source = InputFileFixtures.Read(protocol);
        (protocol switch { LlmProtocol.Responses => source["input"], LlmProtocol.Gemini => source["contents"], _ => source["messages"] })![0]!["role"] = protocol == LlmProtocol.Gemini ? "model" : "assistant";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, protocol, Target(protocol), new() { AllowLoss = true }));
        Assert.Equal("unsupported_file_role", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>来源为通用 Responses URL 时，没有 MIME 数据可以证明另一协议接受相同文件。</summary>
    [Fact]
    public void UnresolvedResponsesUrl_RequiresCallerData()
    {
        var source = InputFileFixtures.Read(LlmProtocol.Responses); InputFileFixtures.Parts(source, LlmProtocol.Responses)[1] =
            CustomToolFixtures.Parse("""{"type":"input_file","file_url":"https://source.invalid/looks-like.pdf"}""");
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, new() { AllowLoss = true }));
        Assert.Equal("media_resolution_required", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>Claude URL 的源协议语义虽明确，但 Chat/Gemini 仍没有此检查点的直接对应 URL 输入。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Gemini)]
    public void ClaudeUrl_RequiresResolutionForOtherTargets(LlmProtocol target)
    {
        var source = InputFileFixtures.Read(LlmProtocol.ClaudeMessages); InputFileFixtures.Parts(source, LlmProtocol.ClaudeMessages)[1]!["source"] =
            CustomToolFixtures.Parse("""{"type":"url","url":"https://source.invalid/file.pdf"}""");
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, target, new() { AllowLoss = true }));
        Assert.Equal("media_resolution_required", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>源 PDF URL 本身必须符合已验证的 HTTP(S) 原生引用契约。</summary>
    [Theory] [InlineData("relative.pdf")] [InlineData("file:///local/document.pdf")]
    public void InvalidPdfUrl_IsNotPassedToUpstream(string url)
    {
        var source = InputFileFixtures.Read(LlmProtocol.ClaudeMessages); InputFileFixtures.Parts(source, LlmProtocol.ClaudeMessages)[1]!["source"] = new JsonObject { ["type"] = "url", ["url"] = url };
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true }));
    }

    /// <summary>原生文档标题、引用/缓存控制不被翻译成文件名或模型提示。</summary>
    [Theory] [InlineData("title", "\"document title\"")] [InlineData("context", "\"context\"")]
    [InlineData("citations", "{\"enabled\":false}")] [InlineData("cache_control", "null")]
    public void ClaudeDocumentMetadata_IsExplicitLoss(string field, string value)
    {
        var source = InputFileFixtures.Read(LlmProtocol.ClaudeMessages); InputFileFixtures.Parts(source, LlmProtocol.ClaudeMessages)[1]![field] = JsonNode.Parse(value);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses));
        Assert.EndsWith("." + field, Assert.Single(error.Diagnostics).Path);
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { AllowLoss = true });
        CustomToolConversionTests.Equal(InputFileFixtures.Read(LlmProtocol.Responses, true), CustomToolConversionTests.Read(result));
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>渲染控制缺少对应字段时，显式 null 也不会自动成为省略而无诊断。</summary>
    [Theory] [InlineData("\"high\"")] [InlineData("null")]
    public void RenderingDetail_IsNotSilentlyDropped(string value)
    {
        var source = InputFileFixtures.Read(LlmProtocol.Responses); InputFileFixtures.File(source, LlmProtocol.Responses)["detail"] = JsonNode.Parse(value);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.Gemini));
        Assert.EndsWith(".detail", Assert.Single(error.Diagnostics).Path);
    }

    /// <summary>构造四种明确绑定于上游的文件引用，不读取磁盘或网络。</summary>
    internal static JsonObject Reference(LlmProtocol protocol)
    {
        var source = InputFileFixtures.Read(protocol); var parts = InputFileFixtures.Parts(source, protocol);
        parts[1] = CustomToolFixtures.Parse(protocol switch
        {
            LlmProtocol.ChatCompletions => """{"type":"file","file":{"file_id":"provider-file"}}""",
            LlmProtocol.Responses => """{"type":"input_file","file_id":"provider-file"}""",
            LlmProtocol.ClaudeMessages => """{"type":"document","source":{"type":"file","file_id":"provider-file"}}""",
            _ => """{"fileData":{"mimeType":"application/pdf","fileUri":"https://generativelanguage.googleapis.com/v1beta/files/provider-file"}}"""
        });
        return source;
    }

    /// <summary>选择与来源不同的原生目标。</summary>
    private static LlmProtocol Target(LlmProtocol source) => source == LlmProtocol.Gemini ? LlmProtocol.Responses : LlmProtocol.Gemini;
}
