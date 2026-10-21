using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>有类型流的业务扩展、真实错误、工具关联与终态必须分别验证。</summary>
public sealed class TypedStreamErrorTests
{
    /// <summary>未知字段的 null、false、零和原生错误样式对象都不能偷换成协议错误事件。</summary>
    public static IEnumerable<object[]> Extensions()
    {
        foreach (var protocol in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Responses })
        foreach (var json in new[] { "null", "false", "0", "[]", "{\"type\":\"overloaded_error\",\"message\":\"business metadata\"}" })
            yield return [protocol, json];
    }

    /// <summary>默认仍严格拒绝未知事件；显式许可只转换已知内容并留下损耗路径。</summary>
    [Theory] [MemberData(nameof(Extensions))]
    public void UnknownTypedEvent_UsesLossPolicyAndIndependentGolden(LlmProtocol protocol, string json)
    {
        var source = TypedStreamErrorFixtures.Source(protocol); source[1]["error"] = JsonNode.Parse(json);
        var before = JsonSerializer.Serialize(source);
        var strict = Assert.Throws<LlmConversionException>(() => TypedStreamErrorFixtures.Convert(protocol, source, false));
        Assert.Contains(strict.Diagnostics, item => item.Code == "unknown_event" && item.Path == "$event.vendor.metrics");
        Assert.DoesNotContain(strict.Diagnostics, item => item.Code == "upstream_stream_error");
        Assert.DoesNotContain("business metadata", strict.Message);
        var results = TypedStreamErrorFixtures.Convert(protocol, source, true);
        EqualGolden(results); Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.Contains(results[^1].Diagnostics, item => item.Path == "$event.error" && item.Code == "unsupported_field");
        Assert.Contains(results[^1].Diagnostics, item => item.Path == "$event.vendor.metrics" && item.Code == "unknown_event");
        var copy = new LlmProtocolStreamConverter(protocol, protocol).Push(source[1]);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source[1]), Assert.Single(copy).OriginalData));
    }

    /// <summary>已知事件上的扩展字段仍是逐字段诊断，产生目标增量时保留完整原始 JSON。</summary>
    [Theory] [MemberData(nameof(Extensions))]
    public void KnownTypedEvent_ExtensionRetainsOriginalData(LlmProtocol protocol, string json)
    {
        var source = TypedStreamErrorFixtures.Source(protocol, false);
        var changed = source.Single(item => item["type"]!.GetValue<string>() ==
            (protocol == LlmProtocol.ClaudeMessages ? "content_block_stop" : "response.function_call_arguments.done"));
        changed["error"] = JsonNode.Parse(json);
        var expected = JsonSerializer.SerializeToElement(changed);
        var strict = Assert.Throws<LlmConversionException>(() => TypedStreamErrorFixtures.Convert(protocol, source, false));
        Assert.Equal("unsupported_field", Assert.Single(strict.Diagnostics).Code);
        var results = TypedStreamErrorFixtures.Convert(protocol, source, true); EqualGolden(results);
        Assert.True(JsonElement.DeepEquals(expected, results[1].OriginalData));
        changed["error"] = "mutated";
        Assert.True(JsonElement.DeepEquals(expected, results[1].OriginalData));
        Assert.Equal("$event.error", Assert.Single(results[^1].Diagnostics).Path);
    }

    /// <summary>只有本协议错误类型可判错；Claude 不借用 Responses 的事件名。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ForeignFailureType_IsUnknownClaudeEvent(bool allowLoss)
    {
        var source = TypedStreamErrorFixtures.Source(LlmProtocol.ClaudeMessages);
        source[1]["type"] = "response.failed";
        if (allowLoss) EqualGolden(TypedStreamErrorFixtures.Convert(LlmProtocol.ClaudeMessages, source, true));
        else
        {
            var failure = Assert.Throws<LlmConversionException>(() => TypedStreamErrorFixtures.Convert(LlmProtocol.ClaudeMessages, source, false));
            Assert.Contains(failure.Diagnostics, item => item.Code == "unknown_event");
            Assert.DoesNotContain(failure.Diagnostics, item => item.Code == "upstream_stream_error");
        }
    }

    /// <summary>错误事件在缺少、空或扩展 payload 时仍不得因损耗许可变成成功。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages, "error", false)] [InlineData(LlmProtocol.ClaudeMessages, "error", true)]
    [InlineData(LlmProtocol.Responses, "error", false)] [InlineData(LlmProtocol.Responses, "error", true)]
    [InlineData(LlmProtocol.Responses, "response.failed", false)] [InlineData(LlmProtocol.Responses, "response.failed", true)]
    public void RealErrorType_AlwaysFailsAndClosesConverter(LlmProtocol protocol, string type, bool payload)
    {
        var converter = new LlmProtocolStreamConverter(protocol, LlmProtocol.ChatCompletions, new() { AllowLoss = true });
        converter.Push(TypedStreamErrorFixtures.Source(protocol)[0]);
        var error = new JsonObject { ["type"] = type }; if (payload) error["error"] = new JsonObject { ["type"] = "business" };
        var failure = Assert.Throws<LlmConversionException>(() => converter.Push(error));
        Assert.Equal("upstream_stream_error", Assert.Single(failure.Diagnostics).Code);
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
        Assert.Throws<InvalidOperationException>(() => converter.Push(error));
    }

    /// <summary>未携带类型的根错误保留防御边界，不能因新增未知事件策略被忽略。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Gemini)]
    public void UntypedRootError_StillFails(LlmProtocol protocol)
    {
        var target = protocol == LlmProtocol.ChatCompletions ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        var converter = new LlmProtocolStreamConverter(protocol, target, new() { AllowLoss = true });
        var failure = Assert.Throws<LlmConversionException>(() => converter.Push(new() { ["error"] = new JsonObject { ["code"] = 429 } }));
        Assert.Equal("upstream_stream_error", Assert.Single(failure.Diagnostics).Code);
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Gemini)
        {
            foreach (var json in new[] { "false", "{}" })
            {
                var next = new LlmProtocolStreamConverter(protocol, target, new() { AllowLoss = true });
                var error = Assert.Throws<LlmConversionException>(() => next.Push(new() { ["type"] = JsonNode.Parse(json), ["error"] = new JsonObject() }));
                Assert.Equal("upstream_stream_error", Assert.Single(error.Diagnostics).Code);
            }
        }
    }

    /// <summary>未知扩展不能替代终态，不能修补工具参数，也不能改变原生工具关联。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, "truncated")] [InlineData(LlmProtocol.Responses, "truncated")]
    [InlineData(LlmProtocol.ClaudeMessages, "arguments")] [InlineData(LlmProtocol.Responses, "arguments")]
    [InlineData(LlmProtocol.ClaudeMessages, "association")] [InlineData(LlmProtocol.Responses, "association")]
    public void LossConsent_DoesNotBypassSourceIntegrity(LlmProtocol protocol, string kind)
    {
        var source = TypedStreamErrorFixtures.Source(protocol);
        if (kind == "truncated") source.RemoveAt(source.Count - 1);
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            if (kind == "arguments") source[4]["delta"]!["partial_json"] = "invalid}";
            else source[4]["index"] = 7;
        }
        else if (kind == "arguments") source[5]["arguments"] = "{}";
        else source[4]["item_id"] = "fc-foreign";
        var failure = Record.Exception(() => TypedStreamErrorFixtures.Convert(protocol, source, true));
        if (kind == "arguments" && protocol == LlmProtocol.ClaudeMessages) Assert.IsAssignableFrom<JsonException>(failure);
        else Assert.IsType<InvalidDataException>(failure);
    }

    /// <summary>完整 Chat 快照同时证明调用 ID、参数数值精度、错误业务对象和原始用量。</summary>
    internal static void EqualGolden(IReadOnlyList<LlmConversionResult> results)
    {
        var expected = TypedStreamErrorFixtures.Read("ChatGolden"); Assert.Equal(expected.Count, results.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected[i]), results[i].Data), results[i].Data.GetRawText());
        var call = results[1].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal("call-original", call.GetProperty("id").GetString());
        using var args = JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!);
        Assert.Equal(9007199254740993L, args.RootElement.GetProperty("n").GetInt64()); Assert.False(args.RootElement.GetProperty("ok").GetBoolean());
    }
}
