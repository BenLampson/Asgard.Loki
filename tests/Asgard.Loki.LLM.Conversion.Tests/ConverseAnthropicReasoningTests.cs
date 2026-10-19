using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>完整独立金样证明签名、加密字节和块顺序不损坏，不验证真实签名或跨模型可移植性。</summary>
public sealed class ConverseAnthropicReasoningTests
{
    /// <summary>空签名字符串和空 Base64 容器不被伪造、移除或当成可验证的密码学证明。</summary>
    [Theory] [InlineData("signed")] [InlineData("redacted")]
    public void EmptyOpaqueValues_RemainExplicitAndUnchanged(string name)
    {
        var sample = Sample(name); var field = name == "signed" ? "signature" : "data";
        sample["sourceRequest"]!["messages"]![1]!["content"]![0]![field] = "";
        var expectedNative = sample["nativeRequest"]!.AsObject(); var requestReasoning = expectedNative["messages"]![1]!["content"]![0]!["reasoningContent"]!;
        var source = sample["nativeResponse"]!.AsObject(); var responseReasoning = source["output"]!["message"]!["content"]![0]!["reasoningContent"]!;
        if (name == "signed") { requestReasoning["reasoningText"]!["signature"] = ""; responseReasoning["reasoningText"]!["signature"] = ""; }
        else { requestReasoning["redactedContent"] = ""; responseReasoning["redactedContent"] = ""; }
        var request = Convert(sample["sourceRequest"]!.AsObject()); Equal(expectedNative, request.Data);
        sample["claudeResponse"]!["content"]![0]![field] = "";
        var result = LlmProtocolConverter.ConvertResponse(source, request.Context!);
        Assert.True(JsonNode.DeepEquals(sample["claudeResponse"], ConverseTextFixtures.NormalizeResponse(result.Data, LlmProtocol.ClaudeMessages)));
        Assert.Empty(result.Diagnostics);
    }
    /// <summary>即使允许普通损耗，携带签名的整段对话也不能丢失已诊断的字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SignedConversation_RejectsOrdinaryLossEvenWithConsent(bool response)
    {
        var source = Sample("signed")[response ? "nativeResponse" : "sourceRequest"]!.AsObject(); source["future_extension"] = null;
        var options = Options with { AllowLoss = true };
        var error = Assert.Throws<LlmConversionException>(() =>
        {
            if (response) return LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockConverse, LlmProtocol.ClaudeMessages, options);
            return LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.BedrockConverse, options);
        });
        Assert.Equal("$.future_extension", Assert.Single(error.Diagnostics).Path);
    }
    /// <summary>连续工具结果消息属于签名历史的一部分，不能为统一格式把它们合并。</summary>
    [Fact]
    public void SignedHistory_KeepsOriginalMessageBoundaries()
    {
        var sample = Sample("mixed-tools"); var source = sample["sourceRequest"]!.AsObject(); var expected = sample["nativeRequest"]!.AsObject();
        var original = source["messages"]!.AsArray().Last()!["content"]!.AsArray(); var native = expected["messages"]!.AsArray().Last()!["content"]!.AsArray();
        source["messages"]!.AsArray().RemoveAt(2); expected["messages"]!.AsArray().RemoveAt(2);
        for (var index = 0; index < original.Count; index++)
        {
            source["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(original[index]!.DeepClone()) });
            expected["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(native[index]!.DeepClone()) });
        }
        Equal(expected, Convert(source).Data);
    }
    /// <summary>不成对的 Unicode 不能在签名或原文序列化时被静默替换。</summary>
    [Theory] [InlineData("thinking")] [InlineData("signature")]
    public void InvalidUnicode_IsNotRepairedInSignedHistory(string field)
    {
        var source = Sample("signed")["sourceRequest"]!.AsObject(); var block = source["messages"]![1]!["content"]![0]!;
        var invalid = new string((char)0xD800, 1); block[field] = invalid;
        Assert.NotNull(Record.Exception(() => Convert(source))); Assert.Equal(invalid, block[field]!.GetValue<string>());
    }
    /// <summary>显式选择同一 Anthropic 模型方言，缺省及其他协议仍不开放不透明内容。</summary>
    private static LlmConversionOptions Options => new() { BedrockConverseModelDialect = BedrockConverseModelDialect.Anthropic };
    /// <summary>每组样本包含完整原生请求、响应和历史回传目标。</summary>
    public static IEnumerable<object[]> Cases() => Samples().Select(item => new object[] { item!["name"]!.GetValue<string>() });
    /// <summary>完整请求、结果和再回传均保持签名/文本/Base64/工具关联的原始顺序和数值。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public void CompleteAndHistoricalReplay_MatchIndependentGoldens(string name)
    {
        var sample = Sample(name); var source = sample["sourceRequest"]!.AsObject(); var before = source.ToJsonString();
        var request = Convert(source); Equal(sample["nativeRequest"]!, request.Data); Equal(source, request.OriginalData);
        Assert.Empty(request.Diagnostics); Assert.Equal(before, source.ToJsonString());
        var native = sample["nativeResponse"]!.AsObject(); var originalReply = native.ToJsonString();
        var response = LlmProtocolConverter.ConvertResponse(native, request.Context!);
        Assert.True(JsonNode.DeepEquals(sample["claudeResponse"], ConverseTextFixtures.NormalizeResponse(response.Data, LlmProtocol.ClaudeMessages)), response.Data.GetRawText());
        Equal(native, response.OriginalData); Assert.Equal(originalReply, native.ToJsonString()); Assert.Empty(response.Diagnostics);
        var followup = sample["followupSourceRequest"]!.AsObject(); var replay = Convert(followup);
        Equal(sample["followupNativeRequest"]!, replay.Data); Assert.Empty(replay.Diagnostics);
        var nativeHistory = replay.Data.GetProperty("messages")[3].GetProperty("content");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(native["output"]!["message"]!["content"]), nativeHistory));
    }

    /// <summary>没有请求上下文时模型仍是未知，原文独立保存；不从签名推测模型身份。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public void DirectResponse_DoesNotInventModelOrRetainMutableNodes(string name)
    {
        var sample = Sample(name); var source = sample["nativeResponse"]!.AsObject();
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockConverse, LlmProtocol.ClaudeMessages, Options);
        var expected = sample["claudeResponse"]!.AsObject(); expected["model"] = "";
        Assert.True(JsonNode.DeepEquals(expected, ConverseTextFixtures.NormalizeResponse(result.Data, LlmProtocol.ClaudeMessages)));
        source.Clear(); Assert.True(result.OriginalData.TryGetProperty("output", out _));
    }

    /// <summary>未选方言、Nova2 以及其他文本协议不能因损耗许可获得 Claude 签名内容。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, BedrockConverseModelDialect.Unspecified)]
    [InlineData(LlmProtocol.ClaudeMessages, BedrockConverseModelDialect.AmazonNova2)]
    [InlineData(LlmProtocol.ChatCompletions, BedrockConverseModelDialect.Anthropic)]
    [InlineData(LlmProtocol.Responses, BedrockConverseModelDialect.Anthropic)]
    [InlineData(LlmProtocol.Gemini, BedrockConverseModelDialect.Anthropic)]
    public void OpaqueReply_DoesNotCrossUndeclaredProtocolBoundary(LlmProtocol target, BedrockConverseModelDialect dialect)
    {
        var native = Sample("signed")["nativeResponse"]!.AsObject();
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(native, LlmProtocol.BedrockConverse, target,
            new() { BedrockConverseModelDialect = dialect, AllowLoss = true }));
    }

    /// <summary>不透明结构的缺字段、扩展或联合分支冲突不能被修复、丢弃或写入异常原文。</summary>
    [Theory] [InlineData("missing-signature")] [InlineData("null-signature")] [InlineData("unknown-field")]
    [InlineData("mixed-union")] [InlineData("outer-union")] [InlineData("invalid-redacted")]
    public void MalformedNativeReasoning_FailsUnderLossConsent(string kind)
    {
        var source = Sample("mixed-tools")["nativeResponse"]!.AsObject(); var block = source["output"]!["message"]!["content"]![0]!.AsObject();
        var reasoning = block["reasoningContent"]!.AsObject(); var text = reasoning["reasoningText"]!.AsObject();
        if (kind == "missing-signature") text.Remove("signature");
        if (kind == "null-signature") text["signature"] = null;
        if (kind == "unknown-field") text["future_secret"] = "MUST_NOT_LEAK";
        if (kind == "mixed-union") reasoning["redactedContent"] = "AQID";
        if (kind == "outer-union") block["text"] = "MUST_NOT_LEAK";
        if (kind == "invalid-redacted") { reasoning.Clear(); reasoning["redactedContent"] = "MUST_NOT_LEAK"; }
        var before = source.ToJsonString(); var error = Record.Exception(() => LlmProtocolConverter.ConvertResponse(source,
            LlmProtocol.BedrockConverse, LlmProtocol.ClaudeMessages, Options with { AllowLoss = true }));
        Assert.NotNull(error); Assert.DoesNotContain("MUST_NOT_LEAK", error.Message); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>历史中错误角色、缺签名、额外字段或坏加密数据不能变成一个有效原生请求。</summary>
    [Theory] [InlineData("role")] [InlineData("missing-signature")] [InlineData("null-text")]
    [InlineData("unknown-field")] [InlineData("invalid-redacted")] [InlineData("no-dialect")]
    public void MalformedClaudeHistory_FailsBeforeDroppingOpaqueState(string kind)
    {
        var source = Sample("mixed-tools")["sourceRequest"]!.AsObject(); var assistant = source["messages"]![1]!;
        var block = assistant["content"]![0]!.AsObject(); var options = Options with { AllowLoss = true };
        if (kind == "role") assistant["role"] = "user";
        if (kind == "missing-signature") block.Remove("signature");
        if (kind == "null-text") block["thinking"] = null;
        if (kind == "unknown-field") block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (kind == "invalid-redacted") assistant["content"]![1]!["data"] = "not base64";
        if (kind == "no-dialect") options = options with { BedrockConverseModelDialect = BedrockConverseModelDialect.Unspecified };
        var before = source.ToJsonString(); Assert.NotNull(Record.Exception(() => LlmProtocolConverter.ConvertRequest(source,
            LlmProtocol.ClaudeMessages, LlmProtocol.BedrockConverse, options))); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>请求上下文仍拦截历史调用 ID 复用，不把推理块误当业务工具参数扫描。</summary>
    [Fact]
    public void SignedContext_StillRejectsReusedToolCallId()
    {
        var sample = Sample("mixed-tools"); var request = Convert(sample["sourceRequest"]!.AsObject());
        var response = sample["nativeResponse"]!.AsObject(); response["output"]!["message"]!["content"]![3]!["toolUse"]!["toolUseId"] = "history-a";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, request.Context!));
        Assert.Equal("tool_call_id_reused", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>始终使用当前测试资源，不通过生产转换生成期望值。</summary>
    private static JsonArray Samples()
    {
        using var stream = typeof(ConverseAnthropicReasoningTests).Assembly.GetManifestResourceStream("ConverseAnthropicReasoning.Fixture")!;
        return JsonNode.Parse(stream)!["cases"]!.AsArray();
    }
    /// <summary>每次独立读取，变体不共享可变节点。</summary>
    private static JsonObject Sample(string name) => Samples().Single(item => item!["name"]!.GetValue<string>() == name)!.AsObject();
    /// <summary>只执行本轮支持的 Claude 到 Converse 请求方向。</summary>
    private static LlmConversionResult Convert(JsonObject source) => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.BedrockConverse, Options);
    /// <summary>完整 JSON 比较包含原始签名、块顺序、数字、空值和 Schema。</summary>
    private static void Equal(JsonNode expected, JsonElement actual) => Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), actual), actual.GetRawText());
}
