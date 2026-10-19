using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>上下文驱动的 custom 事件、资源边界及源流失败的独立协议测试。</summary>
public sealed class CustomToolStreamTests
{
    /// <summary>三种原生流与同一人工目标金样全字段比较，并保留每个触发源事件。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void Golden_ValidatesCompleteEventSequence(LlmProtocol target)
    {
        var request = Prepare(target); var events = Source(request, target); var before = events.Select(e => e.ToJsonString()).ToArray();
        var converter = new LlmProtocolStreamConverter(request.Context!); var output = new List<LlmConversionResult>();
        foreach (var item in events)
        {
            var converted = converter.Push(item); output.AddRange(converted);
            Assert.All(converted, result => CustomToolConversionTests.Equal(item, JsonNode.Parse(result.OriginalData.GetRawText())));
        }
        var tail = converter.Complete(); output.AddRange(tail);
        Assert.All(tail, result => CustomToolConversionTests.Equal(events[^1], JsonNode.Parse(result.OriginalData.GetRawText())));
        CustomToolConversionTests.Equal(new JsonArray(CustomToolStreamFixtures.Expected().Select(e => e.DeepClone()).ToArray()), new JsonArray(output.Select(CustomToolConversionTests.Read).Cast<JsonNode?>().ToArray()));
        Assert.All(output, result => { Assert.Same(request.Context, result.Context); Assert.Empty(result.Diagnostics); });
        Assert.Equal(before, events.Select(e => e.ToJsonString()).ToArray());
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
        Assert.Throws<InvalidOperationException>(() => converter.Push(events[0]));
        // 同一不可变上下文可构造另一个独立流状态机，不能继承前一条流的终态。
        Assert.Equal(output.Count, Run(request, events).Count);
    }

    /// <summary>截断源流可以已有增量，但绝不返回完成或不完整终态冒充已验证的结束。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void MissingTerminal_FailsWithoutTargetTerminal(LlmProtocol target)
    {
        var request = Prepare(target); var source = Source(request, target).ToList();
        source.RemoveAt(target == LlmProtocol.ChatCompletions ? source.Count - 2 : source.Count - 1);
        var converter = new LlmProtocolStreamConverter(request.Context!); var output = source.SelectMany(converter.Push).ToList();
        Assert.Throws<InvalidDataException>(() => converter.Complete()); AssertNoTerminal(output);
        Assert.Throws<InvalidOperationException>(() => converter.Push(source[0]));
    }

    /// <summary>明确长度终态保持 incomplete 项目状态，输入 done、item done 与最终快照一致。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void LengthFinish_PreservesIncompleteStatus(LlmProtocol target)
    {
        var request = Prepare(target); var source = Source(request, target);
        if (target == LlmProtocol.ChatCompletions) source[^2]["choices"]![0]!["finish_reason"] = "length";
        else if (target == LlmProtocol.ClaudeMessages) source[^2]["delta"]!["stop_reason"] = "max_tokens";
        else source[^1]["candidates"]![0]!["finishReason"] = "MAX_TOKENS";
        var output = Run(request, source).Select(CustomToolConversionTests.Read).ToArray();
        Assert.Equal("response.incomplete", output[^1]["type"]!.GetValue<string>());
        var final = output[^1]["response"]!; Assert.Equal("incomplete", final["status"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", final["incomplete_details"]!["reason"]!.GetValue<string>());
        var done = output.Single(e => e["type"]!.GetValue<string>() == "response.custom_tool_call_input.done");
        Assert.Equal(done["input"]!.GetValue<string>(), final["output"]![1]!["input"]!.GetValue<string>());
        foreach (var itemDone in output.Where(e => e["type"]!.GetValue<string>() == "response.output_item.done"))
        {
            var item = itemDone["item"]!; Assert.Equal("incomplete", item["status"]!.GetValue<string>());
            CustomToolConversionTests.Equal(item, final["output"]![itemDone["output_index"]!.GetValue<int>()]);
        }
    }

    /// <summary>空串、Unicode 和 JSON 外观文本精确还原，普通函数仍保留 arguments 事件。</summary>
    [Theory]
    [InlineData("")] [InlineData(" \t\r\n")] [InlineData("你好🙂\"quoted\"\\")] [InlineData("{\"input\":false}")]
    public void ArbitraryText_PreservesExactInput(string text)
    {
        foreach (var target in Targets)
        {
            var request = Prepare(target); var source = Source(request, target); SetArguments(source, target, new JsonObject { ["input"] = text }.ToJsonString());
            var output = Run(request, source).Select(CustomToolConversionTests.Read).ToArray();
            Assert.Equal(text, output.Single(e => Type(e) == "response.custom_tool_call_input.delta")["delta"]!.GetValue<string>());
            Assert.Equal(text, output.Single(e => Type(e) == "response.custom_tool_call_input.done")["input"]!.GetValue<string>());
            Assert.Equal(text, output[^1]["response"]!["output"]![1]!["input"]!.GetValue<string>());
            Assert.Single(output, e => Type(e) == "response.function_call_arguments.delta");
        }
    }

    /// <summary>破坏包装对象必须硬失败，不允许 AllowLoss 抹掉字段或发出成功终态。</summary>
    [Theory]
    [InlineData("{}")] [InlineData("{\"input\":null}")] [InlineData("{\"input\":false}")]
    [InlineData("{\"input\":\"ok\",\"extra\":0}")] [InlineData("[]")]
    public void InvalidWrapper_FailsEveryTarget(string arguments)
    {
        foreach (var target in Targets)
        {
            var request = Prepare(target, CustomToolFixtures.Options with { AllowLoss = true }); var source = Source(request, target); SetArguments(source, target, arguments);
            MustFail(request, source);
        }
    }

    /// <summary>错用上下文、历史 ID 复用、重复调用 ID 和未知名称均不能生成终态。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidAssociation_FailsEveryTarget(int mutation)
    {
        foreach (var target in Targets)
        {
            var request = mutation == 1 ? CustomToolConversionTests.Convert(CustomToolConversionTests.History("text"), target) : Prepare(target);
            var source = Source(request, target);
            if (mutation == 0) request = Prepare(target); // 同名工具，另一次请求的随机映射。
            else if (mutation == 1) SetIdentity(source, target, custom: true, "hist_custom");
            else if (mutation == 2) SetIdentity(source, target, custom: false, "new_custom");
            else SetName(source, target, "undeclared");
            MustFail(request, source);
        }
    }

    /// <summary>普通/custom 返回调用共用总上限，完整响应和流式执行相同配置。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void OutputLimit_IsSharedWithCompleteCalls(LlmProtocol target)
    {
        var exact = Prepare(target, CustomToolFixtures.Options with { MaxToolConversionOutputCalls = 2 });
        Assert.Equal(17, Run(exact, Source(exact, target)).Count);
        var request = Prepare(target, CustomToolFixtures.Options with { MaxToolConversionOutputCalls = 1 });
        MustFail(request, Source(request, target));
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), CustomToolFixtures.Name(CustomToolConversionTests.Read(request), target));
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(native, request.Context!));
    }

    /// <summary>已解码的 UTF-8 输入限制精确到四字节 emoji，封装转义不挤占原始文本预算。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DecodedInputLimit_IsExact(bool oversized)
    {
        foreach (var target in Targets)
        {
            var request = Prepare(target, CustomToolFixtures.Options with { MaxCustomToolInputBytes = 4 }); var source = Source(request, target);
            SetArguments(source, target, new JsonObject { ["input"] = oversized ? "🙂a" : "🙂" }.ToJsonString());
            if (oversized) MustFail(request, source);
            else Assert.Equal("🙂", CustomToolConversionTests.Read(Run(request, source)[^1])["response"]!["output"]![1]!["input"]!.GetValue<string>());
        }
    }

    /// <summary>源参数尚未闭合也必须在追加越界时失败，不能等到终态后才检查内存上限。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void EncodedBufferLimit_FailsBeforeTerminal(LlmProtocol target)
    {
        var request = Prepare(target, CustomToolFixtures.Options with { MaxCustomToolInputBytes = 1 }); var source = Source(request, target);
        var arguments = target == LlmProtocol.Gemini ? new JsonObject { ["input"] = new string('a', 71) }.ToJsonString() : new string(' ', 71);
        SetArguments(source, target, arguments);
        var converter = new LlmProtocolStreamConverter(request.Context!);
        Assert.Throws<LlmConversionException>(() => { foreach (var e in source.Take(target == LlmProtocol.ClaudeMessages ? 5 : 2)) converter.Push(e); });
        Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>异常事件和终态后重复生成内容必须使转换失败，已经返回的增量不获得成功终态。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void ErrorOrDuplicateTerminal_Fails(LlmProtocol target)
    {
        var request = Prepare(target); var source = Source(request, target);
        MustFail(request, source.Append(CustomToolFixtures.Parse("""{"error":{"message":"failed"}}""")));
        MustFail(request, source.Append(source[target == LlmProtocol.ChatCompletions ? source.Length - 2 : source.Length - 1].DeepClone().AsObject()));
    }

    /// <summary>Chat 分段名称和分段参数最终绑定到本次映射，不会提前当成未知普通工具。</summary>
    [Fact]
    public void FragmentedChatName_IsRestoredOnlyWhenComplete()
    {
        var request = Prepare(LlmProtocol.ChatCompletions); var source = Source(request, LlmProtocol.ChatCompletions);
        var name = source[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>();
        source[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["name"] = name[..20];
        source[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["name"] = name[20..];
        Assert.Equal("code", CustomToolConversionTests.Read(Run(request, source)[^1])["response"]!["output"]![1]!["name"]!.GetValue<string>());
    }

    /// <summary>JSON 转义序列可以跨参数片段，解包必须等待完整 JSON，不能按字符串片段直接输出。</summary>
    [Fact]
    public void SplitEscapedSurrogatePair_IsRestoredExactly()
    {
        var request = Prepare(LlmProtocol.ChatCompletions); var source = Source(request, LlmProtocol.ChatCompletions);
        source[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = "{\"input\":\"\\uD83";
        source[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = "D\\uDE42\"}";
        Assert.Equal("🙂", CustomToolConversionTests.Read(Run(request, source)[^1])["response"]!["output"]![1]!["input"]!.GetValue<string>());
    }

    /// <summary>同名普通/custom 工具在流内仍保持原始种类，不能按显示名称互换。</summary>
    [Fact]
    public void SameNameTools_KeepTheirKinds()
    {
        foreach (var target in Targets)
        {
            var sourceRequest = CustomToolFixtures.Request(); sourceRequest["tools"]![1]!["name"] = "code";
            var request = CustomToolConversionTests.Convert(sourceRequest, target);
            var source = Source(request, target).Select(e => CustomToolFixtures.Parse(e.ToJsonString().Replace("\"plain\"", "\"code\"", StringComparison.Ordinal))).ToArray();
            var final = CustomToolConversionTests.Read(Run(request, source)[^1])["response"]!["output"]!;
            Assert.Equal("custom_tool_call", final[1]!["type"]!.GetValue<string>()); Assert.Equal("code", final[1]!["name"]!.GetValue<string>());
            Assert.Equal("function_call", final[2]!["type"]!.GetValue<string>()); Assert.Equal("code", final[2]!["name"]!.GetValue<string>());
        }
    }

    /// <summary>回放完成消息只在转换副本移除源 item 元数据，完整 OriginalData 和调用方输入保持不变。</summary>
    [Fact]
    public void CompletedMessageMetadata_IsRetainedInOriginal()
    {
        foreach (var target in Targets)
        {
            var source = CustomToolFixtures.Request();
            source["input"] = new JsonArray(CustomToolFixtures.Parse("""{"type":"message","id":"source-message","created_by":"actor","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}"""));
            var before = source.ToJsonString(); var request = CustomToolConversionTests.Convert(source, target);
            Assert.Equal(before, source.ToJsonString()); CustomToolConversionTests.Equal(source, JsonNode.Parse(request.OriginalData.GetRawText()));
            Assert.DoesNotContain("source-message", request.Data.GetRawText()); Assert.DoesNotContain("actor", request.Data.GetRawText());
        }
    }

    /// <summary>消息处于未完成状态时不能在重放时冒充完整历史。</summary>
    [Theory]
    [InlineData("in_progress")] [InlineData("incomplete")]
    public void UnfinishedHistoryMessage_Fails(string status)
    {
        var source = CustomToolConversionTests.History("text"); source["input"]![0]!["status"] = status;
        Assert.Throws<LlmConversionException>(() => CustomToolConversionTests.Convert(source));
    }

    /// <summary>前序 Chat 工具尚未完成时，后续文本不能越过它重排输出。</summary>
    [Fact]
    public void BufferedToolInterleaving_FailsExplicitly()
    {
        var request = Prepare(LlmProtocol.ChatCompletions); var source = Source(request, LlmProtocol.ChatCompletions).ToList();
        source.Insert(3, CustomToolFixtures.Parse("""{"id":"reply","model":"model-a","choices":[{"index":0,"delta":{"content":"late"}}]}"""));
        MustFail(request, source);
    }

    /// <summary>无上下文的旧构造器不凭编码前缀猜测 custom 语义。</summary>
    [Fact]
    public void StatelessConstructor_DoesNotGuessCustomTools()
    {
        foreach (var target in Targets)
        {
            var request = Prepare(target); var source = Source(request, target);
            var converter = new LlmProtocolStreamConverter(target, LlmProtocol.Responses);
            var output = source.SelectMany(converter.Push).ToList(); output.AddRange(converter.Complete());
            Assert.All(output, result => Assert.Null(result.Context));
            var events = output.Select(CustomToolConversionTests.Read).ToArray();
            var item = events[^1]["response"]!["output"]![1]!;
            Assert.Equal("function_call", item["type"]!.GetValue<string>());
            Assert.Equal(CustomToolFixtures.Name(CustomToolConversionTests.Read(request), target), item["name"]!.GetValue<string>());
            Assert.DoesNotContain(events, e => Type(e).StartsWith("response.custom_tool_call_input.", StringComparison.Ordinal));
        }
    }

    /// <summary>同协议上下文不激活 custom 转换或限制未知事件，只保留调用方提供的状态引用。</summary>
    [Fact]
    public void NativeContext_PreservesIdentity()
    {
        var request = LlmProtocolConverter.ConvertRequest(CustomToolFixtures.Request(), LlmProtocol.Responses, LlmProtocol.Responses, CustomToolFixtures.Options);
        var converter = new LlmProtocolStreamConverter(request.Context!);
        var source = CustomToolFixtures.Parse("""{"type":"response.custom_tool_call_input.delta","sequence_number":1,"output_index":0,"item_id":"native","delta":"text","future":false}""");
        var result = Assert.Single(converter.Push(source));
        CustomToolConversionTests.Equal(source, CustomToolConversionTests.Read(result)); Assert.Same(request.Context, result.Context); Assert.Empty(converter.Complete());
    }

    /// <summary>非法输出数上限和空上下文在 IO 前配置验证失败。</summary>
    [Fact]
    public void InvalidOutputLimit_FailsConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(LlmProtocol.ChatCompletions, CustomToolFixtures.Options with { MaxToolConversionOutputCalls = 0 }));
        Assert.Throws<ArgumentNullException>(() => new LlmProtocolStreamConverter(null!));
    }

    private static LlmProtocol[] Targets => [LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    /// <summary>请求允许 stream 并保留本次编码映射。</summary>
    internal static LlmConversionResult Prepare(LlmProtocol target, LlmConversionOptions? options = null)
    {
        var request = CustomToolFixtures.Request(); request["stream"] = true; return CustomToolConversionTests.Convert(request, target, options);
    }
    /// <summary>从实际转换结果读取随机名称，只绑定人工样本占位符。</summary>
    internal static JsonObject[] Source(LlmConversionResult request, LlmProtocol target)
        => CustomToolStreamFixtures.Source(target, CustomToolFixtures.Name(CustomToolConversionTests.Read(request), target));
    /// <summary>完整枚举源事件后才完成转换。</summary>
    internal static List<LlmConversionResult> Run(LlmConversionResult request, IEnumerable<JsonObject> source)
    {
        var converter = new LlmProtocolStreamConverter(request.Context!); var output = source.SelectMany(converter.Push).ToList(); output.AddRange(converter.Complete()); return output;
    }
    /// <summary>失败后的转换器不可重用，输出不能含成功或不完整终态。</summary>
    private static void MustFail(LlmConversionResult request, IEnumerable<JsonObject> source)
    {
        var converter = new LlmProtocolStreamConverter(request.Context!); var output = new List<LlmConversionResult>();
        Assert.NotNull(Record.Exception(() => { foreach (var e in source) output.AddRange(converter.Push(e)); output.AddRange(converter.Complete()); }));
        AssertNoTerminal(output); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }
    /// <summary>异常不能伪造成已经验证的协议结束。</summary>
    private static void AssertNoTerminal(IEnumerable<LlmConversionResult> output)
        => Assert.DoesNotContain(output, item => Type(CustomToolConversionTests.Read(item)) is "response.completed" or "response.incomplete");
    private static string Type(JsonObject e) => e["type"]!.GetValue<string>();
    /// <summary>按三个独立原生协议修改封装参数，保留其他事件字段。</summary>
    internal static void SetArguments(JsonObject[] source, LlmProtocol target, string arguments)
    {
        if (target == LlmProtocol.ChatCompletions)
        { source[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = arguments; source[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"] = ""; }
        else if (target == LlmProtocol.ClaudeMessages)
        { source[4]["delta"]!["partial_json"] = arguments; source[5]["delta"]!["partial_json"] = ""; }
        else source[1]["candidates"]![0]!["content"]!["parts"]![0]!["functionCall"]!["args"] = JsonNode.Parse(arguments);
    }
    /// <summary>精确定位 native ID 字段，不按名称猜测关联。</summary>
    private static void SetIdentity(JsonObject[] source, LlmProtocol target, bool custom, string id)
    {
        if (target == LlmProtocol.ChatCompletions) source[custom ? 1 : 2]["choices"]![0]!["delta"]!["tool_calls"]![custom ? 0 : 1]!["id"] = id;
        else if (target == LlmProtocol.ClaudeMessages) source[custom ? 3 : 7]["content_block"]!["id"] = id;
        else source[1]["candidates"]![0]!["content"]!["parts"]![custom ? 0 : 1]!["functionCall"]!["id"] = id;
    }
    /// <summary>返回未声明的编码函数名时必须拒绝，而不是按前缀分类。</summary>
    private static void SetName(JsonObject[] source, LlmProtocol target, string name)
    {
        if (target == LlmProtocol.ChatCompletions) source[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["name"] = name;
        else if (target == LlmProtocol.ClaudeMessages) source[3]["content_block"]!["name"] = name;
        else source[1]["candidates"]![0]!["content"]!["parts"]![0]!["functionCall"]!["name"] = name;
    }
}
