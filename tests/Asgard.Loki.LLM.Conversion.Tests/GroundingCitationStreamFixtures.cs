using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>原生源、完整 Responses 事件和手工核算的 Unicode 偏移表；不使用生产转换器生成预期。</summary>
internal static class GroundingCitationStreamFixtures
{
    /// <summary>每次加载独立嵌入样本，保持测试之间互不影响。</summary>
    internal static JsonNode Read(string key)
    {
        using var stream = typeof(GroundingCitationStreamFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.stream-to-responses.json") ?? throw new InvalidDataException("引用流样本缺失。");
        return JsonNode.Parse(stream)![key]!;
    }
    /// <summary>源拥有三个增量、候选 finish、纯 metadata 尾帧和独立用量尾帧。</summary>
    internal static List<JsonObject> Source() => Read("Source").AsArray().Select(item => item!.AsObject()).ToList();
    /// <summary>只声明已确认的固定端点约定和输出单位，不默认允许损耗。</summary>
    internal static LlmConversionOptions Options(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar) => new()
    { GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart, OpenAiCitationIndexUnit = unit };
    /// <summary>原生位置上的支持元数据，不扫描模型文本或函数参数。</summary>
    internal static JsonObject Metadata(List<JsonObject> source, int at) => source[at]["candidates"]![0]!["groundingMetadata"]!.AsObject();
    /// <summary>完整运行公开转换器，包括源 EOF 之后的验证与目标完成快照。</summary>
    internal static List<LlmConversionResult> Run(IEnumerable<JsonObject> source, LlmConversionOptions? options = null)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.Responses, options ?? Options());
        List<LlmConversionResult> result = []; foreach (var item in source) result.AddRange(converter.Push(item)); result.AddRange(converter.Complete()); return result;
    }
    /// <summary>以独立手算的完整注解列表替换固定快照位置，不包含源范围转换算法。</summary>
    internal static List<JsonObject> Expected(LlmCitationIndexUnit unit)
    {
        var result = Read("Responses").AsArray().Select(node => node!.AsObject()).ToList();
        if (unit == LlmCitationIndexUnit.UnicodeScalar) return result;
        var annotations = Read(unit == LlmCitationIndexUnit.Utf16CodeUnit ? "Utf16Annotations" : "Utf8Annotations").AsArray();
        for (var i = 0; i < 4; i++) result[7 + i]["annotation"] = annotations[i]!.DeepClone();
        result[12]["part"]!["annotations"] = annotations.DeepClone();
        result[13]["item"]!["content"]![0]!["annotations"] = annotations.DeepClone();
        result[14]["response"]!["output"]![0]!["content"]![0]!["annotations"] = annotations.DeepClone();
        return result;
    }
    /// <summary>对比完整 JSON，不依赖序列化属性顺序。</summary>
    internal static void Equal(JsonNode expected, JsonElement actual) => CustomToolConversionTests.Equal(expected, JsonNode.Parse(actual.GetRawText()));
}
