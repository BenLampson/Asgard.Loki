using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>测试专有完整原生样本和目标快照，独立于生产实现。</summary>
internal static class BedrockConversionFixtures
{
    /// <summary>完整资源以手写原生字段维护，不通过转换器生成预期结果。</summary>
    internal static JsonObject Read(string version, string kind)
    {
        using var stream = typeof(BedrockConversionFixtures).Assembly.GetManifestResourceStream("BedrockConversion.Fixture")!;
        return JsonNode.Parse(stream)![version]![kind]!.AsObject();
    }
    /// <summary>外壳和专有设置布局均由调用者明确选择，不猜测模型版本或文档差异。</summary>
    internal static LlmConversionOptions Options(string version = "v2", bool nested = false, bool allowLoss = false) => new()
    {
        BedrockNovaRequestFormat = version == "v1" ? BedrockNovaRequestFormat.MessagesV1 : BedrockNovaRequestFormat.Nova2,
        BedrockConverseInferenceFormat = nested ? BedrockConverseInferenceFormat.NestedInferenceConfig : BedrockConverseInferenceFormat.Direct,
        AllowLoss = allowLoss
    };
}
