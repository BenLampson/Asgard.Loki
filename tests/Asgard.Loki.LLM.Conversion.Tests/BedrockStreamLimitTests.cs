using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.BedrockStreamFixtures;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>SDK 流聚合/尾部/诊断资源有限，缺省选项不改变既有原生完整 JSON 绑定。</summary>
public sealed class BedrockStreamLimitTests
{
    /// <summary>每种状态上限都明确失败，损耗许可不会截断或丢弃状态以继续。</summary>
    [Theory] [InlineData(true, "block")] [InlineData(false, "block")]
    [InlineData(true, "argument")] [InlineData(false, "argument")]
    [InlineData(true, "tail")] [InlineData(false, "tail")]
    [InlineData(true, "diagnostic")] [InlineData(false, "diagnostic")]
    public void ResourceOverflow_RejectsWithoutTerminal(bool nova, string kind)
    {
        var rows = Rows(nova); var options = new LlmConversionOptions { AllowLoss = true };
        if (kind == "block") options = options with { MaxBedrockStreamBlocks = 3 };
        if (kind == "argument") options = options with { MaxBedrockStreamToolArgumentBytes = 4 };
        if (kind == "tail") options = options with { MaxBedrockStreamTailBytes = 32 };
        if (kind == "diagnostic")
        {
            options = options with { MaxBedrockStreamDiagnostics = 2 }; var first = Payload(rows[0], nova);
            first["a"] = null; first["b"] = false; first["c"] = 0;
        }
        var converter = new BedrockProtocolStreamConverter(nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, options); List<BedrockStreamConversionResult> seen = [];
        var failure = Assert.Throws<LlmConversionException>(() => { foreach (var row in rows) seen.AddRange(Push(converter, row)); seen.AddRange(converter.Complete()); });
        Assert.Equal("bedrock_stream_" + kind + "_limit", Assert.Single(failure.Diagnostics).Code);
        Assert.DoesNotContain(seen, item => item.EventType is "messageStop" or "metadata");
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>工具上限按原始参数文本 UTF-8 字节计数，等于上限时不因 JSON 转义或另一调用而误判。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ExactArgumentByteLimit_DoesNotReencodeOrShareBudget(bool nova)
    {
        var rows = Rows(nova); var argument = Payload(rows[5], nova)["delta"]!["toolUse"]!["input"]!.GetValue<string>() + Payload(rows[9], nova)["delta"]!["toolUse"]!["input"]!.GetValue<string>();
        var limit = Encoding.UTF8.GetByteCount(argument); EqualGolden(nova, Convert(nova, rows, new() { MaxBedrockStreamToolArgumentBytes = limit }));
        var failure = Assert.Throws<LlmConversionException>(() => Convert(nova, rows, new() { MaxBedrockStreamToolArgumentBytes = limit - 1 }));
        Assert.Equal("bedrock_stream_argument_limit", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>新增缺省省略选项保持先前精确选项字节；显式策略才进入身份。</summary>
    [Theory] [InlineData("v1")] [InlineData("v2")]
    public void DefaultStreamPolicies_PreservePrecedingOptionBytes(string version)
    {
        var expected = Read("PreStreamOptions").AsObject(); expected["BedrockNovaRequestFormat"] = version == "v1" ? 1 : 2;
        var options = BedrockConversionFixtures.Options(version);
        Assert.Equal(expected.ToJsonString(), JsonSerializer.Serialize(options));
        var explicitOptions = JsonSerializer.Serialize(options with { MaxBedrockStreamBlocks = 128 });
        Assert.Contains("\"MaxBedrockStreamBlocks\":128", explicitOptions);
    }

    /// <summary>无效策略在建立状态前失败，不能等到首次 HTTP 或无限流之后才发现。</summary>
    [Theory] [InlineData("block", 0)] [InlineData("block", -1)] [InlineData("argument", 0)] [InlineData("argument", -1)]
    [InlineData("tail", 0)] [InlineData("tail", -1)] [InlineData("diagnostic", 0)] [InlineData("diagnostic", -1)]
    public void NonPositivePolicies_AreRejected(string kind, int value)
    {
        var options = new LlmConversionOptions();
        options = kind switch { "block" => options with { MaxBedrockStreamBlocks = value }, "argument" => options with { MaxBedrockStreamToolArgumentBytes = value },
            "tail" => options with { MaxBedrockStreamTailBytes = value }, _ => options with { MaxBedrockStreamDiagnostics = value } };
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockProtocolStreamConverter(LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse, options));
    }

    /// <summary>独立原生流转换器不把其他协议、合并标志或 None 解释为该原生事件外壳。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Gemini)] [InlineData(LlmProtocol.None)] [InlineData(LlmProtocol.BedrockNova | LlmProtocol.BedrockConverse)]
    public void OtherProtocolFamilies_RemainExplicitBoundaries(LlmProtocol protocol)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockProtocolStreamConverter(protocol, LlmProtocol.BedrockNova));
}
