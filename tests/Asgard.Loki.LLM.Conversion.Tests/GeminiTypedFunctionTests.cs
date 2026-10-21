using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>函数 typed Schema 与输出 Schema 分开验收，定义转换不能破坏工具历史和回填。</summary>
public sealed class GeminiTypedFunctionTests
{
    /// <summary>三个完整目标报文独立手工指定，调用 ID、参数和结果与 Schema 同时比较。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void FullToolHistory_MatchesIndependentSnapshot(LlmProtocol target)
    {
        var fixture = Fixture(); var source = fixture["Gemini"]!.AsObject(); var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target);
        Assert.Empty(result.Diagnostics);
        Assert.True(JsonNode.DeepEquals(fixture[target.ToString()], JsonNode.Parse(result.Data.GetRawText())), result.Data.GetRawText());
        Assert.Equal(before, source.ToJsonString());
        var replay = LlmProtocolConverter.ConvertRequest(JsonNode.Parse(result.Data.GetRawText())!.AsObject(), target, LlmProtocol.Gemini);
        var history = JsonNode.Parse(replay.Data.GetRawText())!["contents"]!;
        Assert.True(JsonNode.DeepEquals(source["contents"], history));
        Assert.True(JsonNode.DeepEquals(Parameters(fixture[target.ToString()]!.AsObject(), target),
            JsonNode.Parse(replay.Data.GetRawText())!["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]));
    }

    /// <summary>同一个函数不能同时携带两个方言，否则无法决定真实约束。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MixedParameterDialects_AreRejected(bool allowLoss)
    {
        var source = Source(); Function(source)["parametersJsonSchema"] = JsonNode.Parse("""{"type":"object"}""");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { AllowLoss = allowLoss }));
    }

    /// <summary>不能把 typed 参数里未知约束删掉后继续发出一个放宽定义。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownFunctionConstraint_IsHardFailure(bool allowLoss)
    {
        var source = Source(); Function(source)["parameters"]!["additionalProperties"] = false;
        var failure = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Responses, new() { AllowLoss = allowLoss }));
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("unsupported_schema_keyword", diagnostic.Code);
        Assert.Equal("$.tools[].function.parameters.additionalProperties", diagnostic.Path);
    }

    /// <summary>普通函数调用不能直接套用文本格式的输出属性顺序保证。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void FunctionOrdering_RequiresExplicitLossConsent(LlmProtocol target)
    {
        var source = Source(); Function(source)["parameters"]!["propertyOrdering"] = new JsonArray("cap", "items");
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target));
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, target, new() { AllowLoss = true });
        Assert.Equal("schema_property_ordering_not_representable", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(new[] { "cap", "items" }, Parameters(JsonNode.Parse(result.Data.GetRawText())!.AsObject(), target)["properties"]!.AsObject().Select(p => p.Key));
    }

    /// <summary>原生同协议仍完整保留 typed Schema，不能偷偷迁移用户请求。</summary>
    [Fact]
    public void NativeCopy_PreservesTypedDefinition()
    {
        var source = Source(); var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.Gemini, LlmProtocol.Gemini);
        Assert.Equal(source.ToJsonString(), JsonNode.Parse(result.Data.GetRawText())!.ToJsonString());
    }

    /// <summary>读取独立嵌入的完整工具请求/回放快照。</summary>
    internal static JsonObject Fixture()
    {
        using var stream = typeof(GeminiTypedFunctionTests).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.schema.typed-function.json") ?? throw new InvalidDataException("typed 工具样本缺失。");
        return JsonNode.Parse(stream)!.AsObject();
    }
    /// <summary>提取当前协议的函数 JSON Schema。</summary>
    internal static JsonNode Parameters(JsonObject request, LlmProtocol protocol)
        => protocol == LlmProtocol.ChatCompletions ? request["tools"]![0]!["function"]!["parameters"]!
            : request["tools"]![0]![protocol == LlmProtocol.ClaudeMessages ? "input_schema" : "parameters"]!;
    /// <summary>每个测试持有独立原生输入。</summary>
    internal static JsonObject Source() => Fixture()["Gemini"]!.AsObject();
    /// <summary>定位源函数，不修改样本中的其他调用/结果身份。</summary>
    internal static JsonObject Function(JsonObject source) => source["tools"]![0]!["functionDeclarations"]![0]!.AsObject();
}
