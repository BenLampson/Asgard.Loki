using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>namespace 三元身份、完整/流式金样、历史关联和严格损耗边界的离线验证。</summary>
public sealed class NamespacedToolConversionTests
{
    /// <summary>分别比较三种完整原生报文和完整还原响应，不能仅做实现自洽的往返测试。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void Golden_CompleteRequestAndResponse(LlmProtocol target)
    {
        var source = NamespacedToolFixtures.Request(); var before = source.ToJsonString(); var request = Convert(source, target); var wire = Read(request);
        Equal(NamespacedToolFixtures.Bind(NamespacedToolFixtures.ExpectedRequest(target), wire, target), wire);
        var native = NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(target), wire, target);
        var response = LlmProtocolConverter.ConvertResponse(native, request.Context!);
        Equal(NamespacedToolFixtures.ExpectedResponse(), Read(response)); Equal(native, JsonNode.Parse(response.OriginalData.GetRawText()));
        Assert.Equal(3, request.Context!.MappedToolCount); Assert.Equal(3, request.Context.NamespacedToolCount); Assert.Equal(1, request.Context.CustomToolCount);
        Assert.Equal(3, new[] { 2, 3, 4 }.Select(i => NamespacedToolFixtures.Name(wire, target, i)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(before, source.ToJsonString()); Equal(source, JsonNode.Parse(request.OriginalData.GetRawText())); Assert.Empty(request.Diagnostics);
    }

    /// <summary>所有 17 个事件均比较 namespace、类型、序号、项目 ID、输入/参数和最终快照。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void Golden_StreamRestoresBothToolKinds(LlmProtocol target)
    {
        var request = Convert(NamespacedToolFixtures.Request(), target); var events = NamespacedToolFixtures.Stream(Read(request), target);
        var output = CustomToolStreamTests.Run(request, events);
        var expected = NamespacedToolFixtures.ExpectedStream(); Assert.Equal(expected.Length, output.Count);
        for (var i = 0; i < output.Count; i++) { Equal(expected[i], Read(output[i])); Assert.Same(request.Context, output[i].Context); }
    }

    /// <summary>完成消息及 namespace 调用可重放，结果的可选 name/namespace 必须与 call_id 一致。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void History_ReplaysAllOriginalIdentities(LlmProtocol target)
    {
        var source = Replay(); var before = source.ToJsonString(); var request = Convert(source, target); var wire = Read(request);
        var calls = Calls(wire, target).ToDictionary(c => c["id"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal(5, calls.Count);
        foreach (var item in source["input"]!.AsArray().OfType<JsonObject>().Where(i => i["type"]?.GetValue<string>() is "function_call" or "custom_tool_call"))
        {
            var call = calls[item["call_id"]!.GetValue<string>()]; var name = item["name"]!.GetValue<string>(); var scope = item["namespace"]?.GetValue<string>();
            var expectedName = scope == "crm" ? NamespacedToolFixtures.Name(wire, target, name == "code" ? 3 : 2)
                : scope == "support" ? NamespacedToolFixtures.Name(wire, target, 4) : name;
            Assert.Equal(expectedName, call["name"]!.GetValue<string>());
            var expectedArgs = item["input"] is null ? JsonNode.Parse(item["arguments"]!.GetValue<string>()) : new JsonObject { ["input"] = item["input"]!.DeepClone() };
            Equal(expectedArgs, call["args"]);
        }
        Assert.DoesNotContain("\"namespace\":", wire.ToJsonString()); Assert.DoesNotContain("fc_new_", wire.ToJsonString());
        Assert.Equal(before, source.ToJsonString()); Equal(source, JsonNode.Parse(request.OriginalData.GetRawText()));
    }

    /// <summary>namespace 普通函数不要求 custom 开关，也不能错误解包为 custom 输入。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void FunctionsOnly_NeedNoCustomOptIn(LlmProtocol target)
    {
        var source = FunctionsOnly(); var request = Convert(source, target, NamespacedToolFixtures.Options with { EnableCustomToolConversion = false });
        Assert.Equal(0, request.Context!.CustomToolCount); Assert.Equal(2, request.Context.MappedToolCount); Assert.Equal(2, request.Context.NamespacedToolCount);
        var events = FunctionStream(request, target); var output = CustomToolStreamTests.Run(request, events).Select(Read).ToArray();
        Assert.DoesNotContain(output, e => e["type"]!.GetValue<string>().StartsWith("response.custom_tool_call_input.", StringComparison.Ordinal));
        var first = output[^1]["response"]!["output"]![1]!;
        Assert.Equal("function_call", first["type"]!.GetValue<string>()); Assert.Equal("lookup", first["name"]!.GetValue<string>());
        Assert.Equal("crm", first["namespace"]!.GetValue<string>()); Assert.Equal("{\"n\":7}", first["arguments"]!.GetValue<string>());
        var other = Convert(source, target, NamespacedToolFixtures.Options with { EnableCustomToolConversion = false });
        Assert.NotNull(Record.Exception(() => CustomToolStreamTests.Run(other, events)));
    }

    /// <summary>namespace 描述不能悄悄删除或拼入函数描述；只允许有诊断的显式损耗。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void GroupDescription_RequiresDiagnosedConsent(LlmProtocol target)
    {
        var source = NamespacedToolFixtures.Request(); source["tools"]![2]!["description"] = "CRM policy";
        var error = Assert.Throws<LlmConversionException>(() => Convert(source, target));
        Assert.Contains(error.Diagnostics, d => d.Code == "namespace_description_not_representable" && d.Path == "$.tools[2].description");
        var result = Convert(source, target, NamespacedToolFixtures.Options with { AllowLoss = true });
        Assert.Single(result.Diagnostics); Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
        Assert.DoesNotContain("CRM policy", result.Data.GetRawText()); Assert.Contains("CRM", result.Data.GetRawText());
    }

    /// <summary>普通/custom 在同一 namespace 下同名时仍是不同身份。</summary>
    [Fact]
    public void SameNameAndKind_AreSeparate()
    {
        foreach (var target in Targets)
        {
            var source = NamespacedToolFixtures.Request(); source["tools"]![2]!["tools"]![1]!["name"] = "lookup";
            var request = Convert(source, target); var native = NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(target), Read(request), target);
            var result = Read(LlmProtocolConverter.ConvertResponse(native, request.Context!));
            Assert.Equal("lookup", result["output"]![1]!["name"]!.GetValue<string>()); Assert.Equal("function_call", result["output"]![1]!["type"]!.GetValue<string>());
            Assert.Equal("lookup", result["output"]![3]!["name"]!.GetValue<string>()); Assert.Equal("custom_tool_call", result["output"]![3]!["type"]!.GetValue<string>());
        }
    }

    /// <summary>两个三元身份即使朴素双下划线拼接相同，也不能碰撞。</summary>
    [Fact]
    public void SeparatorCollisions_KeepIndependentComponents()
    {
        foreach (var target in Targets)
        {
            var source = FunctionsOnly(); source["tools"]![2]!["name"] = "a__b"; source["tools"]![2]!["tools"]![0]!["name"] = "c";
            source["tools"]![3]!["name"] = "a"; source["tools"]![3]!["tools"]![0]!["name"] = "b__c";
            var request = Convert(source, target); var output = Read(CustomToolStreamTests.Run(request, FunctionStream(request, target))[^1])["response"]!["output"]!;
            Assert.Equal("a__b", output[1]!["namespace"]!.GetValue<string>()); Assert.Equal("c", output[1]!["name"]!.GetValue<string>());
            Assert.Equal("a", output[2]!["namespace"]!.GetValue<string>()); Assert.Equal("b__c", output[2]!["name"]!.GetValue<string>());
        }
    }

    /// <summary>namespace 不是大小写不敏感的账号域，原始大小写不能被规范化。</summary>
    [Fact]
    public void NamespaceCase_IsPreserved()
    {
        var source = NamespacedToolFixtures.Request(); source["tools"]![3]!["name"] = "CRM";
        var request = Convert(source, LlmProtocol.ChatCompletions); var native = NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(LlmProtocol.ChatCompletions), Read(request), LlmProtocol.ChatCompletions);
        var result = Read(LlmProtocolConverter.ConvertResponse(native, request.Context!)); Assert.Equal("CRM", result["output"]![2]!["namespace"]!.GetValue<string>());
    }

    /// <summary>重复组/定义、未知字段、嵌套组、程序调用等安全语义不受 AllowLoss 绕过。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void UnsupportedDefinitions_AlwaysFail(int mutation)
    {
        var source = NamespacedToolFixtures.Request(); var group = source["tools"]![2]!;
        switch (mutation)
        {
            case 0: source["tools"]!.AsArray().Add(group.DeepClone()); break;
            case 1: group["tools"]!.AsArray().Add(group["tools"]![0]!.DeepClone()); break;
            case 2: group["tools"]!.AsArray().Add(group.DeepClone()); break;
            case 3: group["future"] = null; break;
            case 4: group["tools"]![0]!["async"] = false; break;
            case 5: group["tools"]![0]!["allowed_callers"] = new JsonArray("programmatic"); break;
            case 6: group["tools"]![1]!["format"] = CustomToolFixtures.Parse("""{"type":"grammar","syntax":"regex","definition":".*"}"""); break;
            default: group["tools"]![0]!["output_schema"] = new JsonObject(); break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, options: NamespacedToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>结果仍按唯一 ID 关联，并验证原始种类、可选名称及 namespace，不能串到同名组。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void WrongHistoryIdentity_AlwaysFails(int mutation)
    {
        var source = Replay(); var items = source["input"]!.AsArray(); var firstResult = items.OfType<JsonObject>().First(i => i["type"]?.GetValue<string>() == "function_call_output");
        switch (mutation)
        {
            case 0: firstResult["namespace"] = "support"; break;
            case 1: firstResult["name"] = "code"; break;
            case 2: firstResult["type"] = "custom_tool_call_output"; firstResult.Remove("name"); firstResult.Remove("namespace"); break;
            case 3: firstResult["call_id"] = "missing"; break;
            case 4: items.Add(firstResult.DeepClone()); break;
            case 5: items.Add(items[2]!.DeepClone()); break;
            case 6: items[2]!["status"] = "incomplete"; break;
            default: items.OfType<JsonObject>().First(i => i["type"]?.GetValue<string>() == "custom_tool_call_output")["namespace"] = "crm"; break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, options: NamespacedToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>两个选项独立；普通损耗许可也不能充当 namespace/custom 编码的隐式授权。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OptIns_AreIndependent(bool namespaceEnabled)
    {
        Assert.Throws<LlmConversionException>(() => Convert(NamespacedToolFixtures.Request(), options: NamespacedToolFixtures.Options with
        { EnableNamespacedToolConversion = namespaceEnabled, EnableCustomToolConversion = !namespaceEnabled, AllowLoss = true }));
    }

    /// <summary>不猜测官方具名选择未定义的 namespace 选择器、限定名或同名子工具。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NamespacedChoice_IsNotInvented(int choice)
    {
        var source = NamespacedToolFixtures.Request(); source["tool_choice"] = choice switch
        {
            0 => CustomToolFixtures.Parse("""{"type":"function","name":"lookup"}"""),
            1 => CustomToolFixtures.Parse("""{"type":"function","name":"lookup","namespace":"crm"}"""),
            _ => CustomToolFixtures.Parse("""{"type":"custom","name":"code"}""")
        };
        Assert.Throws<LlmConversionException>(() => Convert(source, options: NamespacedToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>不歧义的根函数仍能强制选择，原始名称不能被当作展开前缀。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void RootChoice_IsPreserved(LlmProtocol target)
    {
        var source = NamespacedToolFixtures.Request(); source["tool_choice"] = CustomToolFixtures.Parse("""{"type":"function","name":"crm__lookup"}""");
        var result = Read(Convert(source, target)); var choice = target == LlmProtocol.Gemini ? result["toolConfig"]!["functionCallingConfig"]!["allowedFunctionNames"]![0]
            : target == LlmProtocol.ChatCompletions ? result["tool_choice"]!["function"]!["name"] : result["tool_choice"]!["name"];
        Assert.Equal("crm__lookup", choice!.GetValue<string>());
    }

    /// <summary>表容量计入根普通函数，返回和历史上限覆盖所有 namespace 及种类。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Limits_IncludeAllIdentities(int limit)
    {
        if (limit == 0) Assert.Throws<LlmConversionException>(() => Convert(NamespacedToolFixtures.Request(), options: NamespacedToolFixtures.Options with { MaxToolConversionMappings = 4 }));
        else if (limit == 1) Assert.Throws<LlmConversionException>(() => Convert(Replay(), options: NamespacedToolFixtures.Options with { MaxToolConversionHistoryCalls = 4 }));
        else
        {
            var request = Convert(NamespacedToolFixtures.Request(), options: NamespacedToolFixtures.Options with { MaxToolConversionOutputCalls = 4 });
            Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(LlmProtocol.ChatCompletions), Read(request), LlmProtocol.ChatCompletions), request.Context!));
        }
    }

    /// <summary>工具从当前声明移除后，显式历史仍保留原 namespace 身份，并受同样的表上限约束。</summary>
    [Fact]
    public void HistoricalOnlyNamespaces_AreRetained()
    {
        var source = Replay(); source.Remove("tools"); source.Remove("tool_choice");
        var result = Convert(source); Assert.Equal(3, result.Context!.NamespacedToolCount); Assert.Equal(1, result.Context.CustomToolCount);
        Equal(source, JsonNode.Parse(result.OriginalData.GetRawText()));
    }

    /// <summary>同协议复制保留 namespace、未知字段和原生选择器，不启用任何编码。</summary>
    [Fact]
    public void NativeCopy_IsUnchanged()
    {
        var source = NamespacedToolFixtures.Request(); source["tools"]![2]!["future"] = false;
        source["tool_choice"] = CustomToolFixtures.Parse("""{"type":"function","name":"lookup","namespace":"crm"}""");
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.Responses);
        Equal(source, Read(result)); Assert.Equal(0, result.Context!.MappedToolCount);
    }

    /// <summary>源 namespace 不允许 null/空串/非字符串，不能自动归入根作用域。</summary>
    [Theory]
    [InlineData("null")] [InlineData("\"\"")] [InlineData("0")]
    public void InvalidNamespace_IsNotRoot(string json)
    {
        var source = Replay(); source["input"]![2]!["namespace"] = JsonNode.Parse(json);
        Assert.NotNull(Record.Exception(() => Convert(source, options: NamespacedToolFixtures.Options with { AllowLoss = true })));
    }

    /// <summary>function 结果的 name/namespace 均可省略；存在时才独立校验，关联始终来自 call_id。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OptionalResultMetadata_IsValidatedIndependently(int fields)
    {
        var source = Replay();
        foreach (var result in source["input"]!.AsArray().OfType<JsonObject>().Where(i => i["type"]?.GetValue<string>() == "function_call_output"))
        { if (fields != 0) result.Remove("name"); if (fields != 1) result.Remove("namespace"); }
        var converted = Convert(source); Assert.Equal(3, converted.Context!.NamespacedToolCount); Equal(source, JsonNode.Parse(converted.OriginalData.GetRawText()));
    }

    /// <summary>完整响应也只能使用原请求上下文，还原不能仅靠原始显示名称相同而成功。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void WrongCompleteContext_Fails(LlmProtocol target)
    {
        var first = Convert(NamespacedToolFixtures.Request(), target); var second = Convert(NamespacedToolFixtures.Request(), target);
        var native = NamespacedToolFixtures.Bind(NamespacedToolFixtures.Response(target), Read(first), target);
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(native, second.Context!));
    }

    /// <summary>展开 namespace 不得修改 JSON Schema 中的未知扩展、约束、零/false/null。</summary>
    [Fact]
    public void SchemaConstraints_AreUnchanged()
    {
        foreach (var target in Targets)
        {
            var source = NamespacedToolFixtures.Request(); var schema = CustomToolFixtures.Parse("""{"type":"object","properties":{"n":{"type":"integer","minimum":0}},"additionalProperties":false,"default":null,"x-extra":{"zero":0,"disabled":false}}""");
            source["tools"]![2]!["tools"]![0]!["parameters"] = schema.DeepClone(); var wire = Read(Convert(source, target));
            Equal(schema, target == LlmProtocol.Gemini ? wire["tools"]![0]!["functionDeclarations"]![2]!["parametersJsonSchema"]
                : target == LlmProtocol.ChatCompletions ? wire["tools"]![2]!["function"]!["parameters"] : wire["tools"]![2]!["input_schema"]);
        }
    }

    /// <summary>空 namespace 分组表也有边界，不能用零个子工具绕过名称状态容量。</summary>
    [Fact]
    public void EmptyGroupTable_IsBounded()
    {
        var source = FunctionsOnly(); source["tools"] = new JsonArray(
            CustomToolFixtures.Parse("""{"type":"namespace","name":"a","description":"","tools":[]}"""),
            CustomToolFixtures.Parse("""{"type":"namespace","name":"b","description":"","tools":[]}"""));
        Assert.Throws<LlmConversionException>(() => Convert(source, options: NamespacedToolFixtures.Options with { MaxToolConversionMappings = 1 }));
    }

    /// <summary>全局选择模式对展开后的同一工具集合生效，不删除工具来伪造 none。</summary>
    [Theory]
    [InlineData("auto")] [InlineData("required")] [InlineData("none")]
    public void GlobalChoice_PreservesToolSet(string mode)
    {
        foreach (var target in Targets)
        {
            var source = NamespacedToolFixtures.Request(); source["tool_choice"] = mode; var wire = Read(Convert(source, target));
            var actual = target == LlmProtocol.Gemini ? wire["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>()
                : target == LlmProtocol.ChatCompletions ? wire["tool_choice"]!.GetValue<string>() : wire["tool_choice"]!["type"]!.GetValue<string>();
            Assert.Equal(target == LlmProtocol.Gemini ? mode == "required" ? "ANY" : mode.ToUpperInvariant() : target == LlmProtocol.ClaudeMessages && mode == "required" ? "any" : mode, actual);
            Assert.Equal("lookup", NamespacedToolFixtures.Name(wire, target, 0)); Assert.NotEmpty(NamespacedToolFixtures.Name(wire, target, 4));
        }
    }

    private static LlmProtocol[] Targets => [LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    /// <summary>仅移除 custom 定义，保留同名 namespace 普通函数。</summary>
    internal static JsonObject FunctionsOnly() { var source = NamespacedToolFixtures.Request(); source["tools"]![2]!["tools"]!.AsArray().RemoveAt(1); return source; }
    /// <summary>两个普通函数流，参数无需 custom 文本解包。</summary>
    private static JsonObject[] FunctionStream(LlmConversionResult request, LlmProtocol target)
        => NamespacedToolFixtures.FunctionStream(Read(request), target);
    /// <summary>完整恢复后的所有项与结果一起回放，结果显式名称只用作一致性校验。</summary>
    internal static JsonObject Replay()
    {
        var source = NamespacedToolFixtures.Request(); var items = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "start" });
        foreach (var item in NamespacedToolFixtures.ExpectedResponse()["output"]!.AsArray()) items.Add(item!.DeepClone());
        foreach (var item in items.OfType<JsonObject>().Where(i => i.ContainsKey("call_id")).ToArray())
        {
            var custom = item["type"]!.GetValue<string>() == "custom_tool_call";
            var result = new JsonObject { ["type"] = custom ? "custom_tool_call_output" : "function_call_output", ["call_id"] = item["call_id"]!.DeepClone(), ["output"] = custom ? "ok\n" : "{}" };
            if (!custom) { result["name"] = item["name"]!.DeepClone(); if (item.ContainsKey("namespace")) result["namespace"] = item["namespace"]!.DeepClone(); }
            items.Add(result);
        }
        items.Add(new JsonObject { ["role"] = "user", ["content"] = "again" }); source["input"] = items; return source;
    }
    /// <summary>独立读取三种最终原生报文中的调用字段，统一为断言形状而非使用生产内部表示。</summary>
    private static IEnumerable<JsonObject> Calls(JsonObject wire, LlmProtocol target)
    {
        var messages = target == LlmProtocol.Gemini ? wire["contents"]!.AsArray() : wire["messages"]!.AsArray();
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (target == LlmProtocol.ChatCompletions && message["tool_calls"] is JsonArray calls)
                foreach (var call in calls.OfType<JsonObject>()) yield return new() { ["id"] = call["id"]!.DeepClone(), ["name"] = call["function"]!["name"]!.DeepClone(), ["args"] = JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>()) };
            else if (target == LlmProtocol.ClaudeMessages && message["content"] is JsonArray content)
            { foreach (var call in content.OfType<JsonObject>().Where(c => c["type"]?.GetValue<string>() == "tool_use")) yield return new() { ["id"] = call["id"]!.DeepClone(), ["name"] = call["name"]!.DeepClone(), ["args"] = call["input"]!.DeepClone() }; }
            else if (target == LlmProtocol.Gemini)
                foreach (var part in message["parts"]!.AsArray().OfType<JsonObject>().Where(p => p["functionCall"] is not null))
                { var call = part["functionCall"]!; yield return new() { ["id"] = call["id"]!.DeepClone(), ["name"] = call["name"]!.DeepClone(), ["args"] = call["args"]!.DeepClone() }; }
        }
    }
    /// <summary>使用独立映射配置显式发起请求转换。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol target = LlmProtocol.ChatCompletions, LlmConversionOptions? options = null)
        => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, target, options ?? NamespacedToolFixtures.Options);
    private static JsonObject Read(LlmConversionResult result) => CustomToolConversionTests.Read(result);
    private static void Equal(JsonNode? expected, JsonNode? actual) => CustomToolConversionTests.Equal(expected, actual);
}
