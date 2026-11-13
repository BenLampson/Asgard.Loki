using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>转换时拒绝有歧义的停词，原生透传仍保留供应商请求。</summary>
public sealed class StopSequenceTests
{
    /// <summary>混合数组和非字符串不能静默传给 Claude，也不能在允许损耗时截掉。</summary>
    [Theory]
    [InlineData("42")]
    [InlineData("""["END",null]""")]
    [InlineData("""["END",2]""")]
    public void ChatToClaude_RejectsMalformedStop(string stop)
    {
        var request = Request(stop);
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request,
            LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, new() { AllowLoss = true }));
        var native = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions);
        Assert.Equal(stop, native.Data.GetProperty("stop").GetRawText());
    }

    /// <summary>单个字符串和纯字符串数组都按 Claude 原生数组编码，原顺序不变。</summary>
    [Theory]
    [InlineData("\"END\"", """["END"]""")]
    [InlineData("""["END","STOP"]""", """["END","STOP"]""")]
    public void ChatToClaude_MapsValidStop(string stop, string expected)
    {
        var result = LlmProtocolConverter.ConvertRequest(Request(stop), LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages);
        Assert.Equal(expected, result.Data.GetProperty("stop_sequences").GetRawText());
    }

    /// <summary>构造独立的原生 Chat 请求，保留测试原始停词类型。</summary>
    private static JsonObject Request(string stop) => JsonNode.Parse(
        """{"model":"fixture","messages":[{"role":"user","content":"Hello"}],"max_completion_tokens":32,"stop":"""
        + stop + "}")!.AsObject();
}
