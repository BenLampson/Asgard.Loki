using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>自由文本工具封装、关联和完整响应还原的离线回归测试。</summary>
public sealed class CustomToolConversionTests
{
    /// <summary>三种目标分别与独立人工金样核对，不能通过同一实现的往返掩盖错误。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void CompleteGolden_PreservesBothKinds(LlmProtocol target)
    {
        var source = CustomToolFixtures.Request(); var before = source.ToJsonString();
        var request = Convert(source, target); var wire = Read(request);
        var name = CustomToolFixtures.Name(wire, target);
        Assert.InRange(name.Length, 1, 64); Assert.Matches("^[a-zA-Z0-9_]+$", name);
        Equal(CustomToolFixtures.Bind(CustomToolFixtures.ExpectedRequest(target), name), wire);
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), name);
        var response = LlmProtocolConverter.ConvertResponse(native, request.Context!);
        Equal(CustomToolFixtures.ExpectedResponse(), Read(response));
        Equal(native, JsonNode.Parse(response.OriginalData.GetRawText()));
        Assert.Same(request.Context, response.Context); Assert.Equal(1, request.Context!.CustomToolCount);
        Assert.Equal(LlmProtocol.Responses, request.Context.SourceProtocol); Assert.Equal(target, request.Context.TargetProtocol);
        Assert.Equal(before, source.ToJsonString()); Equal(source, JsonNode.Parse(request.OriginalData.GetRawText()));
        Assert.Empty(request.Diagnostics); Assert.Empty(response.Diagnostics);
    }

    /// <summary>文本外观和编码均不改变语义；已完成 item 可携原 ID/状态重新发送。</summary>
    [Theory]
    [InlineData("")] [InlineData("  \t\r\n")] [InlineData("你好🙂\n\"quoted\"")] [InlineData("{\"input\":false}")]
    public void ArbitraryText_RestoresAndReplays(string text)
    {
        foreach (var target in new[] { LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        {
            var source = History(text); var converted = Convert(source, target); var wire = Read(converted);
            var name = CustomToolFixtures.Name(wire, target);
            Assert.Contains("hist_custom", wire.ToJsonString()); Assert.DoesNotContain("source-item", wire.ToJsonString());
            var args = target switch
            {
                LlmProtocol.ChatCompletions => JsonNode.Parse(wire["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>()),
                LlmProtocol.ClaudeMessages => wire["messages"]![1]!["content"]![0]!["input"],
                _ => wire["contents"]![1]!["parts"]![0]!["functionCall"]!["args"]
            };
            Assert.Equal(text, args!["input"]!.GetValue<string>());
            if (target == LlmProtocol.ChatCompletions) Assert.Equal(text, wire["messages"]![2]!["content"]!.GetValue<string>());
            if (target == LlmProtocol.ClaudeMessages) Assert.Equal(text, wire["messages"]![2]!["content"]![0]!["content"]!.GetValue<string>());
            if (target == LlmProtocol.Gemini) Assert.Equal(text, wire["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["output"]!.GetValue<string>());
            var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), name);
            SetArguments(native, target, new JsonObject { ["input"] = text }.ToJsonString());
            var restored = Read(LlmProtocolConverter.ConvertResponse(native, converted.Context!));
            var item = restored["output"]![1]!.DeepClone(); Assert.Equal(text, item["input"]!.GetValue<string>());
            source["input"]!.AsArray().Add(item);
            source["input"]!.AsArray().Add(CustomToolFixtures.Parse("""{"type":"custom_tool_call_output","call_id":"new_custom","output":"ok"}"""));
            var replay = Convert(source, target);
            Assert.NotEqual(name, CustomToolFixtures.Name(Read(replay), target));
            Equal(source, JsonNode.Parse(replay.OriginalData.GetRawText()));
        }
    }

    /// <summary>映射仅归属一次请求；旧无状态 API 不凭前缀猜测 custom 类型。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void Context_IsRequestLocal(LlmProtocol target)
    {
        var first = Convert(CustomToolFixtures.Request(), target); var second = Convert(CustomToolFixtures.Request(), target);
        var name = CustomToolFixtures.Name(Read(first), target);
        Assert.NotEqual(name, CustomToolFixtures.Name(Read(second), target));
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), name);
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(native, second.Context!));
        var stateless = Read(LlmProtocolConverter.ConvertResponse(native, target, LlmProtocol.Responses));
        Assert.Equal("function_call", stateless["output"]![1]!["type"]!.GetValue<string>());
        Assert.Equal(name, stateless["output"]![1]!["name"]!.GetValue<string>());
    }

    /// <summary>同名普通函数与 custom 不能互相误还原。</summary>
    [Fact]
    public void SameName_DistinguishesToolKinds()
    {
        var source = CustomToolFixtures.Request(); source["tools"]![1]!["name"] = "code";
        var converted = Convert(source); var response = CustomToolFixtures.Bind(CustomToolFixtures.Response(LlmProtocol.ChatCompletions), CustomToolFixtures.Name(Read(converted), LlmProtocol.ChatCompletions));
        response["choices"]![0]!["message"]!["tool_calls"]![1]!["function"]!["name"] = "code";
        var restored = Read(LlmProtocolConverter.ConvertResponse(response, converted.Context!));
        Assert.Equal("custom_tool_call", restored["output"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", restored["output"]![2]!["type"]!.GetValue<string>());
        response["choices"]![0]!["finish_reason"] = "length";
        var incomplete = Read(LlmProtocolConverter.ConvertResponse(response, converted.Context!));
        Assert.Equal("incomplete", incomplete["status"]!.GetValue<string>());
        Assert.Equal("incomplete", incomplete["output"]![1]!["status"]!.GetValue<string>());
        Assert.Equal("custom_tool_call", incomplete["output"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", incomplete["incomplete_details"]!["reason"]!.GetValue<string>());
    }

    /// <summary>额外/缺失/非字符串参数都不能经 AllowLoss 成为成功的 custom 调用。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("{\"input\":null}")] [InlineData("{\"input\":0}")]
    [InlineData("{\"input\":\"text\",\"extra\":false}")] [InlineData("{\"input\":")]
    public void InvalidWrapper_AlwaysFails(string arguments)
    {
        var converted = Convert(CustomToolFixtures.Request(), options: CustomToolFixtures.Options with { AllowLoss = true });
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(LlmProtocol.ChatCompletions), CustomToolFixtures.Name(Read(converted), LlmProtocol.ChatCompletions));
        SetArguments(native, LlmProtocol.ChatCompletions, arguments);
        Assert.NotNull(Record.Exception(() => LlmProtocolConverter.ConvertResponse(native, converted.Context!)));
    }

    /// <summary>CFG/命名空间/异步/未知字段不能被显式损耗开关抹掉。</summary>
    [Theory]
    [InlineData("format", "{\"type\":\"grammar\",\"syntax\":\"regex\",\"definition\":\".*\"}")]
    [InlineData("async", "true")] [InlineData("defer_loading", "false")]
    [InlineData("allowed_callers", "[]")] [InlineData("namespace", "\"ns\"")]
    [InlineData("future", "null")]
    public void UnsupportedSemantics_AlwaysFail(string key, string json)
    {
        var source = CustomToolFixtures.Request(); source["tools"]![0]![key] = JsonNode.Parse(json);
        Assert.Throws<LlmConversionException>(() => Convert(source, options: CustomToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>跨种类结果、孤儿结果、重复 ID 和未完成历史均必须拒绝。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidHistory_Fails(int mutation)
    {
        var source = History("text"); var input = source["input"]!.AsArray();
        switch (mutation)
        {
            case 0: input[2]!["type"] = "function_call_output"; break;
            case 1: input[2]!["call_id"] = "orphan"; break;
            case 2: input.Add(input[1]!.DeepClone()); break;
            case 3: input[1]!["status"] = "in_progress"; break;
            case 4: input[1]!["namespace"] = "ns"; break;
            default: input[2]!["output"] = new JsonArray(); break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, options: CustomToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>上游不能用新的工具调用覆盖历史关联 ID。</summary>
    [Fact]
    public void ReusedHistoryId_Fails()
    {
        var converted = Convert(History("text"));
        var response = CustomToolFixtures.Bind(CustomToolFixtures.Response(LlmProtocol.ChatCompletions), CustomToolFixtures.Name(Read(converted), LlmProtocol.ChatCompletions));
        response["choices"]![0]!["message"]!["tool_calls"]![0]!["id"] = "hist_custom";
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, converted.Context!));
    }

    /// <summary>映射包含普通工具；输入和结果按 UTF-8 字节计数而非字符数。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Bounds_AreEnforced(int limit)
    {
        var source = History("🙂"); var options = limit switch
        {
            0 => CustomToolFixtures.Options with { MaxToolConversionMappings = 1 },
            1 => CustomToolFixtures.Options with { MaxCustomToolInputBytes = 3 },
            2 => CustomToolFixtures.Options with { MaxToolConversionHistoryCalls = 1 },
            _ => CustomToolFixtures.Options with { MaxCustomToolInputBytes = 4 }
        };
        if (limit == 2) source["input"]!.AsArray().Add(CustomToolFixtures.Parse("""{"type":"function_call","call_id":"second","name":"plain","arguments":"{}"}"""));
        if (limit == 3) source["input"]![2]!["output"] = "🙂a";
        Assert.Throws<LlmConversionException>(() => Convert(source, options: options));
    }

    /// <summary>非法限制值在所有转换开始前失败。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void InvalidLimits_FailConfiguration(int limit)
    {
        var options = limit switch { 0 => CustomToolFixtures.Options with { MaxToolConversionMappings = 0 }, 1 => CustomToolFixtures.Options with { MaxToolConversionHistoryCalls = -1 }, _ => CustomToolFixtures.Options with { MaxCustomToolInputBytes = 0 } };
        Assert.Throws<ArgumentOutOfRangeException>(() => Convert(CustomToolFixtures.Request(), options: options));
    }

    /// <summary>定义/选择歧义、名称缺失、混合托管工具和重复结果均不能改变关联语义。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void AmbiguousToolContext_Fails(int mutation)
    {
        var source = History("text");
        switch (mutation)
        {
            case 0: source["tools"]!.AsArray().Add(source["tools"]![0]!.DeepClone()); break;
            case 1: source["tools"]!.AsArray().Add(source["tools"]![1]!.DeepClone()); break;
            case 2: source["tool_choice"]!["name"] = "missing"; break;
            case 3: source["tool_choice"]!["type"] = "function"; break;
            case 4: source["tool_choice"]!["namespace"] = "ns"; break;
            case 5: source["tools"]!.AsArray().Add(CustomToolFixtures.Parse("""{"type":"web_search"}""")); break;
            case 6: source["input"]!.AsArray().Add(source["input"]![2]!.DeepClone()); break;
            case 7: source["input"]![1]!["caller"] = new JsonObject(); break;
            case 8: source["tools"]![0]!["format"]!["extra"] = false; break;
            default: source["tools"]!.AsArray().Add(CustomToolFixtures.Parse("""{"type":"namespace","name":"ns","description":"group","tools":[]}""")); break;
        }
        Assert.Throws<LlmConversionException>(() => Convert(source, options: CustomToolFixtures.Options with { AllowLoss = true }));
    }

    /// <summary>精确 UTF-8 边界允许，响应超限拒绝；普通前缀相似名称不会被误解码。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ResponseBounds_AndOrdinaryPrefixArePreserved(bool overLimit)
    {
        var source = History("🙂"); source["tools"]![1]!["name"] = "loki_custom_fake_0";
        var converted = Convert(source, options: CustomToolFixtures.Options with { MaxCustomToolInputBytes = 4 });
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(LlmProtocol.ChatCompletions), CustomToolFixtures.Name(Read(converted), LlmProtocol.ChatCompletions));
        native["choices"]![0]!["message"]!["tool_calls"]![1]!["function"]!["name"] = "loki_custom_fake_0";
        SetArguments(native, LlmProtocol.ChatCompletions, new JsonObject { ["input"] = overLimit ? "🙂a" : "🙂" }.ToJsonString());
        if (overLimit) Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(native, converted.Context!));
        else
        {
            var restored = Read(LlmProtocolConverter.ConvertResponse(native, converted.Context!));
            Assert.Equal("🙂", restored["output"]![1]!["input"]!.GetValue<string>());
            Assert.Equal("function_call", restored["output"]![2]!["type"]!.GetValue<string>());
            Assert.Equal("loki_custom_fake_0", restored["output"]![2]!["name"]!.GetValue<string>());
        }
    }

    /// <summary>缺省自由文本格式与显式 text 一致，不自动增加严格执行标志。</summary>
    [Fact]
    public void OmittedTextFormat_IsSupported()
    {
        var source = CustomToolFixtures.Request(); source["tools"]![0]!.AsObject().Remove("format");
        var converted = Convert(source); var wire = Read(converted);
        Assert.False(wire["tools"]![0]!["function"]!.AsObject().ContainsKey("strict"));
        Assert.Equal(1, converted.Context!.CustomToolCount);
    }

    /// <summary>默认不开启编码；显式上下文支持流请求，同协议仍完整保留原生结构。</summary>
    [Fact]
    public void OptInAndStreamBoundary_AreExplicit()
    {
        var source = CustomToolFixtures.Request(); Assert.Throws<LlmConversionException>(() => Convert(source, options: new()));
        source["stream"] = true; Assert.True(Read(Convert(source))["stream"]!.GetValue<bool>());
        source["tools"]![0]!["format"] = CustomToolFixtures.Parse("""{"type":"grammar","syntax":"lark","definition":"start: /.+/"}""");
        source["future"] = null;
        Equal(source, Read(LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Responses, LlmProtocol.Responses, CustomToolFixtures.Options)));
    }

    /// <summary>构造带已完成原生 item 元数据的历史。</summary>
    internal static JsonObject History(string text)
    {
        var source = CustomToolFixtures.Request();
        source["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "start" },
            new JsonObject { ["type"] = "custom_tool_call", ["call_id"] = "hist_custom", ["name"] = "code", ["input"] = text, ["id"] = "source-item", ["status"] = "completed", ["created_by"] = "response-old" },
            new JsonObject { ["type"] = "custom_tool_call_output", ["call_id"] = "hist_custom", ["output"] = text },
            new JsonObject { ["role"] = "user", ["content"] = "again" });
        return source;
    }
    /// <summary>按目标的真实字段注入人工参数，便于测试完整响应恢复。</summary>
    internal static void SetArguments(JsonObject response, LlmProtocol target, string arguments)
    {
        if (target == LlmProtocol.ChatCompletions) response["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"] = arguments;
        else if (target == LlmProtocol.ClaudeMessages) response["content"]![1]!["input"] = JsonNode.Parse(arguments);
        else response["candidates"]![0]!["content"]!["parts"]![1]!["functionCall"]!["args"] = JsonNode.Parse(arguments);
    }
    /// <summary>统一调用显式完整请求入口。</summary>
    internal static LlmConversionResult Convert(JsonObject request, LlmProtocol target = LlmProtocol.ChatCompletions, LlmConversionOptions? options = null)
        => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, target, options ?? CustomToolFixtures.Options);
    /// <summary>读取不可变 JSON 结果。</summary>
    internal static JsonObject Read(LlmConversionResult value) => JsonNode.Parse(value.Data.GetRawText())!.AsObject();
    /// <summary>比较完整结构而非只搜索关键字。</summary>
    internal static void Equal(JsonNode? expected, JsonNode? actual) => Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected: {expected}\nActual: {actual}");
}
