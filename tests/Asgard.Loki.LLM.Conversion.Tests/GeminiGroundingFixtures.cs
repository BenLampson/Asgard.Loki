using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立完整 Gemini 来源与六份完整 OpenAI 预期报文，索引由样本文本手工核算。</summary>
internal static class GeminiGroundingFixtures
{
    /// <summary>读取独立嵌入样本，测试预期不调用生产映射代码。</summary>
    internal static JsonObject Read(string key = "Gemini")
    {
        using var stream = typeof(GeminiGroundingFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.web-citations.json") ?? throw new InvalidDataException("grounding 样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsObject();
    }

    /// <summary>两个目标与三种显式计量约定。</summary>
    public static IEnumerable<object[]> Directions()
    {
        foreach (var target in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses })
            foreach (var unit in new[] { LlmCitationIndexUnit.UnicodeScalar, LlmCitationIndexUnit.Utf16CodeUnit, LlmCitationIndexUnit.Utf8Byte })
                yield return [target, unit];
    }

    /// <summary>完整候选的原生元数据，不假设与 Chat 的字段结构相同。</summary>
    internal static JsonObject Metadata(JsonObject source) => source["candidates"]![0]!["groundingMetadata"]!.AsObject();

    /// <summary>声明调用端点单位，不附带损耗许可。</summary>
    internal static LlmConversionOptions Options(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar) => new() { OpenAiCitationIndexUnit = unit };

    /// <summary>完整转换快捷入口，仍走公共转换 API。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol target = LlmProtocol.Responses, LlmConversionOptions? options = null)
        => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, target, options ?? Options());
}
