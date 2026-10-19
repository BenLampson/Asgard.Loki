using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>原生搜索请求配置的金样、范围约束、选择语义与组合能力声明。</summary>
public sealed class HostedSearchConversionTests
{
    /// <summary>仅列出已验证的六个搜索方向；Chat 专用搜索模型没有被当成可互换工具。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol> Directions
    {
        get { TheoryData<LlmProtocol, LlmProtocol> data = []; foreach (var from in Protocols) foreach (var to in Protocols) if (from != to) data.Add(from, to); return data; }
    }
    /// <summary>全部原生目标报文独立比对，并确保请求及 OriginalData 完整保留。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void Golden_UsesNativeHostedTools(LlmProtocol source, LlmProtocol target)
    {
        var request = HostedSearchFixtures.Request(source); var before = request.ToJsonString(); var result = Convert(request, source, target);
        CustomToolConversionTests.Equal(HostedSearchFixtures.Expected(target), CustomToolConversionTests.Read(result));
        CustomToolConversionTests.Equal(request, JsonNode.Parse(result.OriginalData.GetRawText())); Assert.Equal(before, request.ToJsonString()); Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain("function", result.Data.GetRawText());
    }

    /// <summary>未声明或声明了不同上游类型时，普通损耗许可不能代替搜索能力的显式选择。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void TargetDeclaration_IsMandatory(LlmProtocol source, LlmProtocol target)
    {
        var request = HostedSearchFixtures.Request(source);
        Assert.Throws<LlmConversionException>(() => Convert(request, source, target, new() { AllowLoss = true }));
        var mismatch = HostedSearchFixtures.Options(target) with { HostedSearchTarget = target == LlmProtocol.Responses ? LlmHostedSearchTarget.GeminiGoogleSearch : LlmHostedSearchTarget.ResponsesWebSearch };
        Assert.Throws<LlmConversionException>(() => Convert(request, source, target, mismatch with { AllowLoss = true }));
    }

    /// <summary>允许域名和显式地理位置只在具备对应原生字段的两个协议间原样传递。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DomainsAndLocation_ArePreserved(bool reverse)
    {
        var from = reverse ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses; var to = reverse ? LlmProtocol.Responses : LlmProtocol.ClaudeMessages;
        var source = HostedSearchFixtures.Request(from); var tool = source["tools"]![0]!;
        var domains = new JsonArray("Example.com", "docs.example.org"); var location = CustomToolFixtures.Parse("""{"type":"approximate","country":"GB","city":"London","region":"London","timezone":"Europe/London"}""");
        if (reverse) tool["allowed_domains"] = domains.DeepClone(); else tool["filters"] = new JsonObject { ["allowed_domains"] = domains.DeepClone() };
        if (!reverse) tool["external_web_access"] = true; // 允许在线访问，不伪造缓存/实时新鲜度保证。
        tool["user_location"] = location.DeepClone(); var result = CustomToolConversionTests.Read(Convert(source, from, to));
        CustomToolConversionTests.Equal(domains, reverse ? result["tools"]![0]!["filters"]!["allowed_domains"] : result["tools"]![0]!["allowed_domains"]);
        CustomToolConversionTests.Equal(location, result["tools"]![0]!["user_location"]);
    }

    /// <summary>搜索上下文大小不能被猜成 max_uses 或 token 比例；只允许有诊断的显式省略。</summary>
    [Theory] [InlineData("low")] [InlineData("medium")] [InlineData("high")]
    public void ContextSize_RequiresLossConsent(string size)
    {
        foreach (var target in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        {
            var source = HostedSearchFixtures.Request(LlmProtocol.Responses); source["tools"]![0]!["search_context_size"] = size;
            Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Responses, target));
            var result = Convert(source, LlmProtocol.Responses, target, HostedSearchFixtures.Options(target) with { AllowLoss = true });
            var diagnostic = Assert.Single(result.Diagnostics); Assert.Equal("search_context_size_not_representable", diagnostic.Code);
            Assert.Equal("$.tools[0].search_context_size", diagnostic.Path); Assert.DoesNotContain("max_uses", result.Data.GetRawText());
        }
    }

    /// <summary>范围、离线权限和搜索次数不能在 AllowLoss 下变成不受限的在线搜索。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void AccessAndCountConstraints_AreHardBoundaries(int constraint)
    {
        var from = constraint is 0 or 1 or 2 ? LlmProtocol.Responses : constraint == 6 ? LlmProtocol.Gemini : LlmProtocol.ClaudeMessages;
        var to = constraint is 0 or 4 ? LlmProtocol.ClaudeMessages : constraint is 1 or 2 ? LlmProtocol.Gemini : LlmProtocol.Responses;
        var source = HostedSearchFixtures.Request(from); var tool = source["tools"]![0]!;
        switch (constraint)
        {
            case 0: tool["external_web_access"] = false; break;
            case 1: tool["filters"] = CustomToolFixtures.Parse("""{"allowed_domains":["example.com"]}"""); break;
            case 2: tool["user_location"] = CustomToolFixtures.Parse("""{"country":"GB"}"""); break;
            case 3: tool["blocked_domains"] = new JsonArray("example.com"); break;
            case 4: tool["max_uses"] = 0; to = LlmProtocol.Gemini; break;
            case 5: tool["max_uses"] = 5; break;
            default: tool["googleSearch"]!["timeRangeFilter"] = CustomToolFixtures.Parse("""{"startTime":"2026-01-01T00:00:00Z","endTime":"2026-02-01T00:00:00Z"}"""); break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, from, to, HostedSearchFixtures.Options(to) with { AllowLoss = true }));
    }

    /// <summary>路径/通配符/空集合不会被规范化成更宽的域名范围。</summary>
    [Theory] [InlineData("example.com/path")] [InlineData("*.example.com")] [InlineData("https://example.com")] [InlineData("127.0.0.1")]
    public void NonDomainFilters_AreRejected(string domain)
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.ClaudeMessages); source["tools"]![0]!["allowed_domains"] = new JsonArray(domain);
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, HostedSearchFixtures.Options(LlmProtocol.Responses) with { AllowLoss = true }));
    }

    /// <summary>旧版本、动态过滤、图像搜索及未知配置必须保留为原生边界。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void UnsupportedSettings_CannotBeDiscarded(int mutation)
    {
        var from = mutation < 2 ? LlmProtocol.Responses : mutation < 4 ? LlmProtocol.ClaudeMessages : LlmProtocol.Gemini;
        var source = HostedSearchFixtures.Request(from); var tool = source["tools"]![0]!;
        switch (mutation)
        {
            case 0: tool["type"] = "web_search_preview"; break;
            case 1: tool["return_token_budget"] = 0; break;
            case 2: tool["type"] = "web_search_20260209"; break;
            case 3: tool["allowed_callers"] = new JsonArray("code_execution_20260120"); break;
            case 4: tool["googleSearch"]!["searchTypes"] = CustomToolFixtures.Parse("""{"imageSearch":{}}"""); break;
            case 5: tool["googleSearch"]!["future"] = null; break;
            case 6: tool["googleSearch"]!["excludeDomains"] = new JsonArray("example.com"); break;
            default: tool.AsObject().Remove("googleSearch"); tool["googleSearchRetrieval"] = new JsonObject(); break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, from, LlmProtocol.Responses == from ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses,
            HostedSearchFixtures.Options(from == LlmProtocol.Responses ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses) with { AllowLoss = true }));
    }

    /// <summary>Google Search 不能由 functionCallingConfig 强制或关闭；其他原生目标保留明确选择且不删工具。</summary>
    [Theory] [InlineData("auto")] [InlineData("required")] [InlineData("none")] [InlineData("search")]
    public void Choice_UsesNativeSearchSemantics(string choice)
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.Responses); source["tool_choice"] = choice == "search" ? CustomToolFixtures.Parse("""{"type":"web_search"}""") : JsonValue.Create(choice);
        var target = CustomToolConversionTests.Read(Convert(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages));
        Assert.Equal(choice switch { "required" => "any", "search" => "tool", _ => choice }, target["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Single(target["tools"]!.AsArray());
        if (choice == "auto") Assert.Single(CustomToolConversionTests.Read(Convert(source, LlmProtocol.Responses, LlmProtocol.Gemini))["tools"]!.AsArray());
        else Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Responses, LlmProtocol.Gemini, HostedSearchFixtures.Options(LlmProtocol.Gemini) with { AllowLoss = true }));
    }

    /// <summary>函数组合能力声明与搜索声明独立，打开后保留普通函数身份与参数。</summary>
    [Theory] [MemberData(nameof(Directions))]
    public void FunctionCombination_RequiresCallerDeclaration(LlmProtocol from, LlmProtocol to)
    {
        var source = HostedSearchFixtures.Request(from);
        var function = CustomToolFixtures.Parse(from == LlmProtocol.Gemini ? """{"functionDeclarations":[{"name":"lookup","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}}]}"""
            : from == LlmProtocol.ClaudeMessages ? """{"name":"lookup","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}}"""
            : """{"type":"function","name":"lookup","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}""");
        source["tools"]!.AsArray().Add(function);
        Assert.Throws<LlmConversionException>(() => Convert(source, from, to));
        var result = Convert(source, from, to, HostedSearchFixtures.Options(to) with { AllowHostedSearchWithFunctionTools = true });
        Assert.Contains("lookup", result.Data.GetRawText()); Assert.Contains("integer", result.Data.GetRawText()); Assert.Equal(2, result.Data.GetProperty("tools").GetArrayLength());
    }

    /// <summary>搜索与 namespace/custom 各自保留独立开关和请求局部映射，托管工具不会进入函数名称表。</summary>
    [Fact]
    public void NamespaceCustomCombination_RetainsItsContext()
    {
        var source = NamespacedToolFixtures.Request(); source["tools"]!.AsArray().Add(CustomToolFixtures.Parse("""{"type":"web_search"}"""));
        var options = NamespacedToolFixtures.Options with { HostedSearchTarget = LlmHostedSearchTarget.ClaudeWebSearch20250305, AllowHostedSearchWithFunctionTools = true };
        var result = Convert(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, options);
        Assert.Equal(3, result.Context!.MappedToolCount); Assert.Equal(3, result.Context.NamespacedToolCount); Assert.Equal(1, result.Context.CustomToolCount);
        Assert.Equal(6, result.Data.GetProperty("tools").GetArrayLength()); Assert.Equal("web_search", result.Data.GetProperty("tools")[5].GetProperty("name").GetString());
    }

    /// <summary>同协议分支保留新版原生搜索、未知字段和配置，枚举配置本身仍需有效。</summary>
    [Fact]
    public void NativeCopy_PreservesUnsupportedSearchDetails()
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.ClaudeMessages); source["tools"]![0]!["type"] = "web_search_20260318"; source["tools"]![0]!["response_inclusion"] = "excluded";
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.ClaudeMessages)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, new() { HostedSearchTarget = (LlmHostedSearchTarget)999 }));
    }

    /// <summary>同一 Google 工具对象里的函数声明不能因提取 googleSearch 而丢失。</summary>
    [Fact]
    public void CombinedGeminiToolObject_PreservesFunctions()
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.Gemini);
        source["tools"]![0]!["functionDeclarations"] = new JsonArray(CustomToolFixtures.Parse("""{"name":"lookup","parametersJsonSchema":{"type":"object","properties":{}}}"""));
        var result = Convert(source, LlmProtocol.Gemini, LlmProtocol.Responses, HostedSearchFixtures.Options(LlmProtocol.Responses) with { AllowHostedSearchWithFunctionTools = true });
        Assert.Equal(2, result.Data.GetProperty("tools").GetArrayLength()); Assert.Equal("lookup", result.Data.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("web_search", result.Data.GetProperty("tools")[1].GetProperty("type").GetString());
    }

    /// <summary>显式经典 direct 与纯文本 webSearch 类型都按已知原生语义处理，不启用动态代码或图像搜索。</summary>
    [Fact]
    public void ExplicitDirectAndWebTypes_UseNativeDefaults()
    {
        foreach (var sourceProtocol in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        {
            var source = HostedSearchFixtures.Request(sourceProtocol);
            if (sourceProtocol == LlmProtocol.ClaudeMessages) source["tools"]![0]!["allowed_callers"] = new JsonArray("direct");
            else source["tools"]![0]!["googleSearch"]!["searchTypes"] = CustomToolFixtures.Parse("""{"webSearch":{}}""");
            var result = Convert(source, sourceProtocol, LlmProtocol.Responses);
            CustomToolConversionTests.Equal(HostedSearchFixtures.Expected(LlmProtocol.Responses), CustomToolConversionTests.Read(result));
            CustomToolConversionTests.Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        }
    }

    /// <summary>空/显式 null 过滤或没有位置分量的对象不被当成已省略的无限制配置。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ExplicitEmptyOrNullSettings_DoNotBroadenSearch(int setting)
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.Responses); var tool = source["tools"]![0]!;
        if (setting == 0) tool["filters"] = null;
        else if (setting == 1) tool["filters"] = new JsonObject { ["allowed_domains"] = new JsonArray() };
        else if (setting == 2) tool["user_location"] = null;
        else tool["user_location"] = CustomToolFixtures.Parse("""{"type":"approximate"}""");
        Assert.NotNull(Record.Exception(() => Convert(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, HostedSearchFixtures.Options(LlmProtocol.ClaudeMessages) with { AllowLoss = true })));
    }

    /// <summary>Chat 专用搜索模型与 Google 函数选择器均不能冒充统一的搜索触发模式。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnverifiedTriggerOrParallelSemantics_AreRejected(int setting)
    {
        var source = setting == 0 ? ConversionFixtures.Request(LlmProtocol.ChatCompletions) : HostedSearchFixtures.Request(LlmProtocol.Gemini);
        if (setting == 0) source["web_search_options"] = new JsonObject();
        else source["toolConfig"] = CustomToolFixtures.Parse(setting == 1 ? """{"functionCallingConfig":{"mode":"NONE"}}""" : """{"functionCallingConfig":{"mode":"ANY"}}""");
        Assert.Throws<LlmConversionException>(() => Convert(source, setting == 0 ? LlmProtocol.ChatCompletions : LlmProtocol.Gemini,
            LlmProtocol.Responses, HostedSearchFixtures.Options(LlmProtocol.Responses) with { AllowLoss = true }));
    }

    /// <summary>重复搜索工具和原生并行控制都不能由普通损耗选项悄悄合并或删除。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DuplicateOrParallelControl_Fails(bool parallel)
    {
        var source = HostedSearchFixtures.Request(LlmProtocol.Responses);
        if (parallel) source["parallel_tool_calls"] = false; else source["tools"]!.AsArray().Add(source["tools"]![0]!.DeepClone());
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, HostedSearchFixtures.Options(LlmProtocol.ClaudeMessages) with { AllowLoss = true }));
    }

    private static LlmProtocol[] Protocols => [LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    /// <summary>统一显式搜索配置入口。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
        => LlmProtocolConverter.ConvertRequest(source, from, to, options ?? HostedSearchFixtures.Options(to));
}
