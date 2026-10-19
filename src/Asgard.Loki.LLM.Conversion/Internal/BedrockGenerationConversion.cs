using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;
using static Asgard.Loki.LLM.Conversion.Internal.BedrockConversion;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>明确转换共有生成参数和 Nova 专有设置的位置，不估算预算或自动修正参数。</summary>
internal static class BedrockGenerationConversion
{
    /// <summary>只在显式所选位置读取和写入 Nova 设置，错误位置不能因损耗许可被悄悄丢掉。</summary>
    internal static void Map(JsonObject source, JsonObject target, bool fromNova, LlmConversionOptions options, ConversionDocument doc)
    {
        var inference = source.ContainsKey("inferenceConfig") ? Object(source["inferenceConfig"]) : new JsonObject();
        var common = Pick(inference, doc, "$.inferenceConfig", fromNova
            ? ["maxTokens", "temperature", "topP", "stopSequences", "topK", "reasoningConfig"]
            : ["maxTokens", "temperature", "topP", "stopSequences"]);
        JsonObject native = [];
        if (fromNova)
        {
            foreach (var key in new[] { "topK", "reasoningConfig" }) if (common.Remove(key, out var value)) native[key] = value;
        }
        else
        {
            if (inference.ContainsKey("topK") || inference.ContainsKey("reasoningConfig")) Reject("$.inferenceConfig", "bedrock_inference_layout_mismatch");
            if (source.ContainsKey("additionalModelRequestFields"))
            {
                var additional = Object(source["additionalModelRequestFields"]);
                var nested = options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.NestedInferenceConfig;
                if (nested)
                {
                    if (additional.ContainsKey("topK") || additional.ContainsKey("reasoningConfig")) Reject("$.additionalModelRequestFields", "bedrock_inference_layout_mismatch");
                    Check(additional, doc, "$.additionalModelRequestFields", "inferenceConfig");
                    if (additional.ContainsKey("inferenceConfig")) native = Pick(Object(additional["inferenceConfig"]), doc, "$.additionalModelRequestFields.inferenceConfig", "topK", "reasoningConfig");
                }
                else
                {
                    if (additional.ContainsKey("inferenceConfig")) Reject("$.additionalModelRequestFields.inferenceConfig", "bedrock_inference_layout_mismatch");
                    native = Pick(additional, doc, "$.additionalModelRequestFields", "topK", "reasoningConfig");
                }
            }
        }
        if (native.Count != 0 && options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.Unspecified)
            throw new ArgumentException("Nova 专有参数必须显式选择 BedrockConverseInferenceFormat。", nameof(options));
        var nativePath = fromNova ? "$.inferenceConfig" : options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.NestedInferenceConfig
            ? "$.additionalModelRequestFields.inferenceConfig" : "$.additionalModelRequestFields";
        Validate(common, native, doc, nativePath);
        if (fromNova)
        {
            if (source.ContainsKey("inferenceConfig")) target["inferenceConfig"] = common;
            if (native.Count != 0) target["additionalModelRequestFields"] = options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.Direct
                ? native : new JsonObject { ["inferenceConfig"] = native };
        }
        else
        {
            foreach (var field in native) common[field.Key] = field.Value?.DeepClone();
            if (source.ContainsKey("inferenceConfig") || common.Count != 0) target["inferenceConfig"] = common;
        }
    }

    /// <summary>保留明确的关闭/强度；禁止采样与 high 冲突时擅自删字段，也不对输出上限做调整。</summary>
    private static void Validate(JsonObject common, JsonObject native, ConversionDocument doc, string path)
    {
        if (common.ContainsKey("maxTokens")) { Nonnegative(common["maxTokens"]); if (Number(common["maxTokens"]) == 0) throw new ArgumentException("maxTokens 必须为正整数。"); }
        foreach (var key in new[] { "temperature", "topP" })
            if (common.ContainsKey(key)) { var number = JsonSerializer.SerializeToElement(common[key]); if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var value) || !double.IsFinite(value)) throw new ArgumentException("采样设置必须为有限 JSON 数值。"); }
        if (common.ContainsKey("stopSequences")) foreach (var item in Array(common["stopSequences"])) _ = Text(item);
        if (native.ContainsKey("topK")) Nonnegative(native["topK"]);
        if (!native.ContainsKey("reasoningConfig")) return;
        var reasoning = Object(native["reasoningConfig"]);
        var mode = Text(reasoning["type"]); if (mode is not ("enabled" or "disabled")) throw new ArgumentException("未知 Nova 推理模式。");
        var copy = Pick(reasoning, doc, path + ".reasoningConfig", "type", "maxReasoningEffort");
        if (reasoning.ContainsKey("maxReasoningEffort"))
        {
            var effort = Text(reasoning["maxReasoningEffort"]);
            if (effort is not ("low" or "medium" or "high") || mode == "disabled") throw new ArgumentException("Nova 推理强度与显式模式不一致。");
            if (effort == "high" && (common.ContainsKey("temperature") || common.ContainsKey("topP") || native.ContainsKey("topK")))
                throw new ArgumentException("Nova high 推理不能与 temperature/topP/topK 同时使用；转换不会自动删参数。");
        }
        else if (mode == "enabled") throw new ArgumentException("启用 Nova 推理必须保留明确强度。");
        native["reasoningConfig"] = copy;
    }
}
