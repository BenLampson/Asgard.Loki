using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立验证推理强度、精确预算、输出意图和 Schema 的原生路径。</summary>
public sealed class GenerationSettingsTests
{
    /// <summary>四协议的定性强度通过各自正式字段互转，不通过模型名尾缀猜测。</summary>
    [Theory]
    [MemberData(nameof(Directions))]
    public void Effort_MapsAllDirections(LlmProtocol from, LlmProtocol to)
    {
        var source = Request(from);
        SetEffort(source, from, "medium");
        var result = Convert(source, from, to);
        Assert.Equal("medium", Effort(result, to).ToLowerInvariant());
        Assert.Equal("upstream-high", result["model"]!.GetValue<string>());
        Assert.False(result.ToJsonString().Contains("budget_tokens", StringComparison.Ordinal));
    }

    /// <summary>Schema 原文在四协议间完整迁移；不支持的包装元数据仍有诊断。</summary>
    [Theory]
    [MemberData(nameof(Directions))]
    public void JsonSchema_MapsWithoutRewritingConstraints(LlmProtocol from, LlmProtocol to)
    {
        var source = Request(from); var schema = JsonNode.Parse("""{"type":"object","properties":{"n":{"type":["integer","null"],"minimum":0}},"required":["n"],"additionalProperties":false}""")!;
        SetFormat(source, from, schema);
        var converted = Convert(source, from, to, new() { DefaultSchemaName = "result", AllowLoss = true });
        Assert.True(JsonNode.DeepEquals(schema, Schema(converted, to)));
        Assert.True(JsonNode.DeepEquals(schema, Schema(source, from)));
    }

    /// <summary>Claude 的精确手动预算直接进入 Gemini，不变成比例或 effort。</summary>
    [Fact]
    public void ExactBudget_IsPreserved()
    {
        var source = Request(LlmProtocol.ClaudeMessages);
        source["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048,"display":"omitted"}""");
        var target = Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini);
        Assert.Equal(2048, target["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
        Assert.False(target["generationConfig"]!["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>());
    }

    /// <summary>零预算与 false 输出标记保留，不能被当成未提供而忽略。</summary>
    [Fact]
    public void DisabledThinking_PreservesZeroAndFalse()
    {
        var source = Request(LlmProtocol.Gemini);
        source["generationConfig"] = JsonNode.Parse("""{"thinkingConfig":{"thinkingBudget":0,"includeThoughts":false}}""");
        var target = Convert(source, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages);
        Assert.Equal("disabled", target["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("omitted", target["thinking"]!["display"]!.GetValue<string>());
    }

    /// <summary>无法表示的精确预算不得自动变成高强度或成功的无推理请求。</summary>
    [Fact]
    public void BudgetToEffort_RequiresExplicitReplacementAndLossConsent()
    {
        var source = Request(LlmProtocol.ClaudeMessages); source["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048}""");
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses));
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { TargetReasoningEffort = "high" }));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses,
            new() { TargetReasoningEffort = "high", AllowLoss = true });
        Assert.Equal("high", result.Data.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Contains(result.Diagnostics, d => d.Code == "reasoning_budget_replaced");
    }

    /// <summary>显式预算不能反过来把输出上限静默调大。</summary>
    [Fact]
    public void ManualThinking_RejectsInsufficientOutputLimit()
    {
        var source = Request(LlmProtocol.ChatCompletions); SetEffort(source, LlmProtocol.ChatCompletions, "high");
        source["max_completion_tokens"] = 1024;
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages,
            new() { TargetThinkingBudgetTokens = 2048 }));
    }

    /// <summary>没有 Schema 名称时，要求调用者明确提供而不是生成虚假名称。</summary>
    [Fact]
    public void UnnamedSchema_RequiresExplicitNameForOpenAi()
    {
        var source = Request(LlmProtocol.ClaudeMessages); SetFormat(source, LlmProtocol.ClaudeMessages, JsonNode.Parse("""{"type":"object"}""")!);
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses));
    }

    /// <summary>每个异协议方向独立进入测试。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol> Directions
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol> data = [];
            foreach (var a in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
            foreach (var b in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
                if (a != b) data.Add(a, b);
            return data;
        }
    }

    /// <summary>去除无关媒体和工具，单独验证生成配置。</summary>
    internal static JsonObject Request(LlmProtocol p) => JsonNode.Parse(p switch
    {
        LlmProtocol.ChatCompletions => """{"model":"upstream-high","max_completion_tokens":4096,"messages":[{"role":"user","content":"hi"}]}""",
        LlmProtocol.Responses => """{"model":"upstream-high","max_output_tokens":4096,"input":"hi"}""",
        LlmProtocol.ClaudeMessages => """{"model":"upstream-high","max_tokens":4096,"messages":[{"role":"user","content":"hi"}]}""",
        _ => """{"model":"upstream-high","contents":[{"role":"user","parts":[{"text":"hi"}]}]}"""
    })!.AsObject();

    /// <summary>各协议的官方定性强度字段。</summary>
    internal static void SetEffort(JsonObject r, LlmProtocol p, string effort)
    {
        if (p == LlmProtocol.ChatCompletions) r["reasoning_effort"] = effort;
        else if (p == LlmProtocol.Responses) r["reasoning"] = new JsonObject { ["effort"] = effort };
        else if (p == LlmProtocol.ClaudeMessages) r["output_config"] = new JsonObject { ["effort"] = effort };
        else r["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = effort.ToUpperInvariant() } };
    }
    /// <summary>读取目标强度，避免只检查包含某个字符串。</summary>
    internal static string Effort(JsonObject r, LlmProtocol p) => (p switch { LlmProtocol.ChatCompletions => r["reasoning_effort"], LlmProtocol.Responses => r["reasoning"]!["effort"], LlmProtocol.ClaudeMessages => r["output_config"]!["effort"], _ => r["generationConfig"]!["thinkingConfig"]!["thinkingLevel"] })!.GetValue<string>();
    /// <summary>构造独立原生 Schema 配置。</summary>
    internal static void SetFormat(JsonObject r, LlmProtocol p, JsonNode schema)
    {
        if (p == LlmProtocol.ChatCompletions) r["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "result", ["schema"] = schema.DeepClone(), ["strict"] = true } };
        else if (p == LlmProtocol.Responses) r["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "result", ["schema"] = schema.DeepClone(), ["strict"] = true } };
        else if (p == LlmProtocol.ClaudeMessages) r["output_config"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() } };
        else r["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json", ["responseJsonSchema"] = schema.DeepClone() };
    }
    /// <summary>读取实际目标 Schema，验证 JSON 原文。</summary>
    internal static JsonNode Schema(JsonObject r, LlmProtocol p) => (p switch { LlmProtocol.ChatCompletions => r["response_format"]!["json_schema"]!["schema"], LlmProtocol.Responses => r["text"]!["format"]!["schema"], LlmProtocol.ClaudeMessages => r["output_config"]!["format"]!["schema"], _ => r["generationConfig"]!["responseJsonSchema"] })!;
    /// <summary>调用被测公共纯数据接口。</summary>
    internal static JsonObject Convert(JsonObject r, LlmProtocol from, LlmProtocol to, LlmConversionOptions? o = null)
        => JsonNode.Parse(LlmProtocolConverter.ConvertRequest(r, from, to, (o ?? new()) with { DefaultMaxTokens = o?.DefaultMaxTokens ?? 4096 }).Data.GetRawText())!.AsObject();
}
