using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证 Chat 原生搜索的配置、触发语义、范围及组合硬边界。</summary>
public sealed class ChatSearchConversionTests
{
    /// <summary>完整原生报文和反向回放保留消息、位置、输出上限与必需搜索，不把搜索当作函数。</summary>
    [Theory] [MemberData(nameof(ChatSearchFixtures.Directions), MemberType = typeof(ChatSearchFixtures))]
    public void Golden_PreservesRequiredSearchAndLocation(LlmProtocol from, LlmProtocol to)
    {
        var source = ChatSearchFixtures.Read(from); var before = source.ToJsonString();
        var result = ChatSearchFixtures.Convert(source, from, to);
        CustomToolConversionTests.Equal(ChatSearchFixtures.Read(to, true), CustomToolConversionTests.Read(result));
        Assert.Empty(result.Diagnostics); Assert.Equal(before, source.ToJsonString());
        CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        var replay = ChatSearchFixtures.Convert(CustomToolConversionTests.Read(result), to, from);
        CustomToolConversionTests.Equal(ChatSearchFixtures.Read(from, true), CustomToolConversionTests.Read(replay));
        Assert.DoesNotContain("function", result.Data.GetRawText());
    }

    /// <summary>显式空 Chat 搜索对象仍然启用先搜索语义，不要求补默认位置或上下文大小。</summary>
    [Theory] [MemberData(nameof(ChatSearchFixtures.Directions), MemberType = typeof(ChatSearchFixtures))]
    public void EmptySettings_DoNotInventDefaults(LlmProtocol from, LlmProtocol to)
    {
        var source = ChatSearchFixtures.Read(from); ChatSearchFixtures.Settings(source, from).Remove("user_location");
        var expected = ChatSearchFixtures.Read(to, true); ChatSearchFixtures.Settings(expected, to).Remove("user_location");
        var result = ChatSearchFixtures.Convert(source, from, to);
        CustomToolConversionTests.Equal(expected, CustomToolConversionTests.Read(result)); Assert.Empty(result.Diagnostics);
    }

    /// <summary>目标声明只描述配置，不为没有搜索工具的普通请求自动开启搜索。</summary>
    [Fact]
    public void TargetDeclaration_DoesNotEnableUnrequestedSearch()
    {
        var source = ConversionFixtures.Request(LlmProtocol.Responses);
        var ordinary = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        var declared = ChatSearchFixtures.Convert(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        CustomToolConversionTests.Equal(CustomToolConversionTests.Read(ordinary), CustomToolConversionTests.Read(declared));
        Assert.False(CustomToolConversionTests.Read(declared).ContainsKey("web_search_options"));
    }

    /// <summary>Chat 与 Responses 的上下文大小具有相同原生含义，不能转成 token 预算或搜索次数。</summary>
    [Theory] [InlineData("low")] [InlineData("medium")] [InlineData("high")]
    public void ContextSize_RoundtripsWithoutApproximation(string size)
    {
        foreach (var from in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses })
        {
            var to = from == LlmProtocol.ChatCompletions ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
            var source = ChatSearchFixtures.Read(from); ChatSearchFixtures.Settings(source, from)["search_context_size"] = size;
            var result = ChatSearchFixtures.Convert(source, from, to); Assert.Empty(result.Diagnostics);
            var data = CustomToolConversionTests.Read(result); Assert.Equal(size, ChatSearchFixtures.Settings(data, to)["search_context_size"]!.GetValue<string>());
            Assert.DoesNotContain("max_uses", result.Data.GetRawText());
        }
    }

    /// <summary>Claude 没有上下文大小字段，保留搜索要求且仅省略被明确许可的指导值。</summary>
    [Theory] [InlineData("low")] [InlineData("medium")] [InlineData("high")]
    public void ContextSize_ToClaudeNeedsDiagnosedConsent(string size)
    {
        var source = ChatSearchFixtures.Read(LlmProtocol.ChatCompletions); source["web_search_options"]!["search_context_size"] = size;
        Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages));
        var result = ChatSearchFixtures.Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages,
            ChatSearchFixtures.Options(LlmProtocol.ClaudeMessages) with { AllowLoss = true });
        Assert.Equal("search_context_size_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("$.web_search_options.search_context_size", result.Diagnostics[0].Path);
        CustomToolConversionTests.Equal(ChatSearchFixtures.Read(LlmProtocol.ClaudeMessages, true), CustomToolConversionTests.Read(result));
    }

    /// <summary>没有声明或声明不匹配时，即便允许损耗也不能启用原生搜索。</summary>
    [Theory] [MemberData(nameof(ChatSearchFixtures.Directions), MemberType = typeof(ChatSearchFixtures))]
    public void TargetDeclaration_IsRequired(LlmProtocol from, LlmProtocol to)
    {
        var source = ChatSearchFixtures.Read(from);
        foreach (var declaration in new[] { LlmHostedSearchTarget.None, LlmHostedSearchTarget.GeminiGoogleSearch })
        {
            var error = Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, from, to,
                new() { HostedSearchTarget = declaration, AllowLoss = true }));
            Assert.Equal(declaration == LlmHostedSearchTarget.None ? "hosted_search_conversion_not_enabled" : "hosted_search_target_mismatch", Assert.Single(error.Diagnostics).Code);
            Assert.Equal(from == LlmProtocol.ChatCompletions ? "$.web_search_options" : "$.tools[0]", error.Diagnostics[0].Path);
        }
    }

    /// <summary>auto、none 或默认选择不能变为 Chat 的必需搜索；不能靠 AllowLoss 改变触发语义。</summary>
    [Theory] [InlineData("absent")] [InlineData("auto")] [InlineData("none")]
    public void OptionalOrDisabledSearch_CannotBecomeChatSearch(string choice)
    {
        foreach (var sourceProtocol in new[] { LlmProtocol.Responses, LlmProtocol.ClaudeMessages })
        {
            var source = ChatSearchFixtures.Read(sourceProtocol);
            if (choice == "absent") source.Remove("tool_choice");
            else source["tool_choice"] = sourceProtocol == LlmProtocol.Responses ? JsonValue.Create(choice) : new JsonObject { ["type"] = choice };
            var error = Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, sourceProtocol, LlmProtocol.ChatCompletions,
                ChatSearchFixtures.Options(LlmProtocol.ChatCompletions) with { AllowLoss = true }));
            Assert.Equal("hosted_search_choice_not_representable", Assert.Single(error.Diagnostics).Code);
        }
    }

    /// <summary>只有唯一搜索工具时 required/any 才等价于明确指定搜索。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void RequiredOnlySearch_UsesChatNativeTrigger(LlmProtocol sourceProtocol)
    {
        var source = ChatSearchFixtures.Read(sourceProtocol);
        source["tool_choice"] = sourceProtocol == LlmProtocol.Responses ? JsonValue.Create("required") : new JsonObject { ["type"] = "any" };
        CustomToolConversionTests.Equal(ChatSearchFixtures.Read(LlmProtocol.ChatCompletions, true),
            CustomToolConversionTests.Read(ChatSearchFixtures.Convert(source, sourceProtocol, LlmProtocol.ChatCompletions)));
    }

    /// <summary>Chat 与 Gemini 无可验证的强制搜索对应项，任何方向都不能擅改成自动或必需搜索。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void GeminiPair_KeepsHardTriggerBoundary(bool reverse)
    {
        var from = reverse ? LlmProtocol.Gemini : LlmProtocol.ChatCompletions; var to = reverse ? LlmProtocol.ChatCompletions : LlmProtocol.Gemini;
        var source = reverse ? HostedSearchFixtures.Request(from) : ChatSearchFixtures.Read(from);
        if (!reverse) source["web_search_options"] = new JsonObject();
        var error = Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, from, to, ChatSearchFixtures.Options(to) with { AllowLoss = true }));
        Assert.Equal("hosted_search_choice_not_representable", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>混合函数组合即便具备旧三协议的组合声明，也不自动扩展为 Chat 搜索模型的组合支持。</summary>
    [Theory] [MemberData(nameof(ChatSearchFixtures.Directions), MemberType = typeof(ChatSearchFixtures))]
    public void FunctionCombination_IsNotAssumed(LlmProtocol from, LlmProtocol to)
    {
        var source = ChatSearchFixtures.Read(from); var tools = source["tools"] as JsonArray;
        if (tools is null) source["tools"] = tools = new JsonArray();
        tools.Add(ConversionFixtures.Request(from)["tools"]![0]!.DeepClone());
        var error = Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, from, to,
            ChatSearchFixtures.Options(to) with { AllowLoss = true, AllowHostedSearchWithFunctionTools = true }));
        Assert.Equal("chat_search_function_combination_not_supported", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>域名、访问与调用上限不能因为 Chat 没有对应字段而被放宽。</summary>
    [Theory] [InlineData("allowed")] [InlineData("blocked")] [InlineData("offline")] [InlineData("count")]
    public void Constraints_CannotBeDroppedForChat(string constraint)
    {
        var from = constraint is "blocked" or "count" ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses;
        var source = ChatSearchFixtures.Read(from); var tool = source["tools"]![0]!;
        switch (constraint)
        {
            case "allowed": tool["filters"] = new JsonObject { ["allowed_domains"] = new JsonArray("example.org") }; break;
            case "blocked": tool["blocked_domains"] = new JsonArray("example.org"); break;
            case "offline": tool["external_web_access"] = false; break;
            case "count": tool["max_uses"] = 1; break;
        }
        Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, from, LlmProtocol.ChatCompletions,
            ChatSearchFixtures.Options(LlmProtocol.ChatCompletions) with { AllowLoss = true }));
    }

    /// <summary>未知键与无效配置始终显式失败，不能按 false/null 判定为已省略。</summary>
    [Theory] [InlineData("future-null")] [InlineData("future-false")] [InlineData("flat-location")]
    [InlineData("location-type")] [InlineData("location-extra")] [InlineData("context")]
    [InlineData("tool-choice")] [InlineData("parallel")] [InlineData("null-options")]
    public void InvalidOrUnsupportedChatSettings_AreRejected(string mutation)
    {
        var source = ChatSearchFixtures.Read(LlmProtocol.ChatCompletions); var settings = source["web_search_options"]!;
        switch (mutation)
        {
            case "future-null": settings["future"] = null; break;
            case "future-false": settings["future"] = false; break;
            case "flat-location": settings["user_location"] = new JsonObject { ["type"] = "approximate", ["country"] = "GB" }; break;
            case "location-type": settings["user_location"]!["type"] = "exact"; break;
            case "location-extra": settings["user_location"]!["approximate"]!["future"] = false; break;
            case "context": settings["search_context_size"] = "huge"; break;
            case "tool-choice": source["tool_choice"] = "none"; break;
            case "parallel": source["parallel_tool_calls"] = false; break;
            case "null-options": source["web_search_options"] = null; break;
        }
        if (mutation is "context" or "null-options") Assert.Throws<ArgumentException>(() => ChatSearchFixtures.Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            ChatSearchFixtures.Options(LlmProtocol.Responses) with { AllowLoss = true }));
        else Assert.Throws<LlmConversionException>(() => ChatSearchFixtures.Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            ChatSearchFixtures.Options(LlmProtocol.Responses) with { AllowLoss = true }));
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions)));
    }
}
