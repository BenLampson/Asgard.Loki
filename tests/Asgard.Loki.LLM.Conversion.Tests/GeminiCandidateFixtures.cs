using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立读取完整候选样本；预期值从不调用生产转换器生成。</summary>
internal static class GeminiCandidateFixtures
{
    /// <summary>每次读取均返回独立 JSON，不让变异测试修改共享预期。</summary>
    internal static JsonObject Case(string name) => Read("gemini-chat-complete", name);
    /// <summary>在同一独立样本目录读取已提交的请求或反向响应。</summary>
    internal static JsonObject Read(string file, string name)
    {
        var assembly = typeof(GeminiCandidateFixtures).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".candidates." + file + ".json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var root = JsonNode.Parse(stream)!.AsObject();
        return root["cases"]!.AsArray().Select(n => n!.AsObject()).Single(n => n["name"]!.GetValue<string>() == name);
    }
    /// <summary>显式声明 OpenAI 引用单位，不从文本或模型名称推断。</summary>
    internal static LlmConversionOptions Options(bool allowLoss = false) => new()
    { AllowLoss = allowLoss, OpenAiCitationIndexUnit = LlmCitationIndexUnit.Utf16CodeUnit };
    /// <summary>比较完整 JSON；候选和工具数组顺序同样属于契约。</summary>
    internal static void Equal(JsonNode expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual.GetRawText())), $"Expected: {expected}\nActual: {actual}");
    /// <summary>运行被测方向，不混入请求或流式转换的支持声明。</summary>
    internal static LlmConversionResult Convert(JsonObject source, bool allowLoss = false)
        => LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Gemini, LlmProtocol.ChatCompletions, Options(allowLoss));
}
