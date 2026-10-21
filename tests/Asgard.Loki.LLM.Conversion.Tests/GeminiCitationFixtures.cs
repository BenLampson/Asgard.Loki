using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>反向引用转换的独立完整报文，包含重复文本、重复来源和穿插函数。</summary>
internal static class GeminiCitationFixtures
{
    /// <summary>读取六份原生源响应和两份手工核算字节偏移的 Gemini 预期响应。</summary>
    internal static JsonObject Read(string key)
    {
        using var stream = typeof(GeminiCitationFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.openai-to-gemini.json") ?? throw new InvalidDataException("反向引用样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsObject();
    }

    /// <summary>按原生位置读取引用，不把函数参数中相同的字段名当作注解。</summary>
    internal static IEnumerable<JsonObject> Citations(JsonObject source, LlmProtocol protocol)
        => protocol == LlmProtocol.ChatCompletions
            ? source["choices"]![0]!["message"]!["annotations"]!.AsArray().Select(a => a!["url_citation"]!.AsObject())
            : source["output"]!.AsArray().OfType<JsonObject>().Where(i => i["type"]!.GetValue<string>() == "message")
                .SelectMany(i => i["content"]!.AsArray()).SelectMany(p => p!["annotations"]!.AsArray()).Select(a => a!.AsObject());

    /// <summary>走公共完整响应转换入口，仅声明已知单位，不默认允许损耗。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol protocol, LlmCitationIndexUnit unit,
        bool allowLoss = false) => LlmProtocolConverter.ConvertResponse(source, protocol, LlmProtocol.Gemini,
            GeminiGroundingFixtures.Options(unit) with { AllowLoss = allowLoss });
}
