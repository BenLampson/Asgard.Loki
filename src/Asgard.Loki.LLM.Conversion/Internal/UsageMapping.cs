using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>按各协议计数口径转换用量，不引入价格、额度或计费规则。</summary>
internal static class UsageMapping
{
    /// <summary>标准化计数：输入包含缓存，输出包含推理；源协议独有明细单独保存。</summary>
    internal static void Read(JsonNode? value, LlmProtocol protocol, ConversionDocument doc)
    {
        if (value is null) return;
        var usage = Object(value);
        switch (protocol)
        {
            case LlmProtocol.ChatCompletions: case LlmProtocol.Responses:
                var chat = protocol == LlmProtocol.ChatCompletions;
                var input = chat ? "prompt_tokens" : "input_tokens"; var output = chat ? "completion_tokens" : "output_tokens";
                var inputDetails = chat ? "prompt_tokens_details" : "input_tokens_details"; var outputDetails = chat ? "completion_tokens_details" : "output_tokens_details";
                Check(usage, doc, "$usage", input, output, "total_tokens", inputDetails, outputDetails);
                Copy(usage, input, doc.Usage, "input"); Copy(usage, output, doc.Usage, "output"); Copy(usage, "total_tokens", doc.Usage, "total");
                if (usage[inputDetails] is not null)
                {
                    var ins = Object(usage[inputDetails]);
                    Check(ins, doc, "$usage." + inputDetails, "cached_tokens", "audio_tokens", "text_tokens");
                    Copy(ins, "cached_tokens", doc.Usage, "cached");
                    doc.Usage["inputDetails"] = ins.DeepClone();
                    ValidateDetails(ins);
                }
                if (usage[outputDetails] is not null)
                {
                    var outs = Object(usage[outputDetails]);
                    Check(outs, doc, "$usage." + outputDetails, "reasoning_tokens", "audio_tokens", "text_tokens", "accepted_prediction_tokens", "rejected_prediction_tokens");
                    Copy(outs, "reasoning_tokens", doc.Usage, "reasoning");
                    doc.Usage["outputDetails"] = outs.DeepClone();
                    ValidateDetails(outs);
                }
                break;
            case LlmProtocol.ClaudeMessages:
                Check(usage, doc, "$usage", "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens", "cache_creation");
                Copy(usage, "input_tokens", doc.Usage, "claudeInput"); Copy(usage, "output_tokens", doc.Usage, "output");
                Copy(usage, "cache_read_input_tokens", doc.Usage, "cached"); Copy(usage, "cache_creation_input_tokens", doc.Usage, "createdCache");
                if (doc.Usage.ContainsKey("claudeInput")) doc.Usage["input"] = Count(doc, "claudeInput") + Count(doc, "cached") + Count(doc, "createdCache");
                if (usage["cache_creation"] is not null)
                {
                    var creation = Object(usage["cache_creation"]);
                    Check(creation, doc, "$usage.cache_creation", "ephemeral_5m_input_tokens", "ephemeral_1h_input_tokens");
                    doc.Usage["cacheCreationDetails"] = creation.DeepClone();
                    ValidateDetails(creation);
                }
                break;
            case LlmProtocol.Gemini:
                Check(usage, doc, "$usage", "promptTokenCount", "candidatesTokenCount", "totalTokenCount", "cachedContentTokenCount", "thoughtsTokenCount");
                Copy(usage, "promptTokenCount", doc.Usage, "input"); Copy(usage, "candidatesTokenCount", doc.Usage, "geminiOutput");
                Copy(usage, "totalTokenCount", doc.Usage, "total"); Copy(usage, "cachedContentTokenCount", doc.Usage, "cached"); Copy(usage, "thoughtsTokenCount", doc.Usage, "reasoning");
                if (doc.Usage.ContainsKey("geminiOutput")) doc.Usage["output"] = Count(doc, "geminiOutput") + Count(doc, "reasoning");
                break;
        }
        foreach (var field in doc.Usage.Where(f => f.Value is JsonValue))
            if (Number(field.Value) < 0) throw new InvalidDataException("Token 计数不能为负数。");
    }

    /// <summary>以目标协议的输入、缓存和推理统计口径生成结果。</summary>
    internal static JsonObject Write(ConversionDocument doc, LlmProtocol protocol, bool requireComplete = true)
    {
        var result = new JsonObject();
        if (protocol == LlmProtocol.ClaudeMessages)
        {
            if (requireComplete && (doc.Usage["input"] is null || doc.Usage["output"] is null))
                throw new LlmConversionException([new("missing_usage", "$usage", "Claude 完整响应需要输入和输出计数，不能凭空补造。")]);
            if (doc.Usage.ContainsKey("input"))
            {
                var uncached = Count(doc, "input") - Count(doc, "cached") - Count(doc, "createdCache");
                if (uncached < 0) throw new InvalidDataException("缓存 token 数超过输入总量。");
                result["input_tokens"] = uncached;
            }
            Copy(doc.Usage, "output", result, "output_tokens"); Copy(doc.Usage, "cached", result, "cache_read_input_tokens");
            Copy(doc.Usage, "createdCache", result, "cache_creation_input_tokens"); Copy(doc.Usage, "cacheCreationDetails", result, "cache_creation");
            if (Count(doc, "reasoning") != 0) Loss(doc, "$usage.reasoning_tokens");
            DiagnoseDetails(doc, "inputDetails", "cached_tokens"); DiagnoseDetails(doc, "outputDetails", "reasoning_tokens");
            return result;
        }
        if (protocol == LlmProtocol.Gemini)
        {
            Copy(doc.Usage, "input", result, "promptTokenCount");
            if (doc.Usage.ContainsKey("output"))
            {
                var candidates = Count(doc, "output") - Count(doc, "reasoning");
                if (candidates < 0) throw new InvalidDataException("推理 token 数超过输出总量。");
                result["candidatesTokenCount"] = candidates;
            }
            Copy(doc.Usage, "cached", result, "cachedContentTokenCount"); Copy(doc.Usage, "reasoning", result, "thoughtsTokenCount");
            DiagnoseDetails(doc, "inputDetails", "cached_tokens"); DiagnoseDetails(doc, "outputDetails", "reasoning_tokens");
        }
        else
        {
            var chat = protocol == LlmProtocol.ChatCompletions;
            Copy(doc.Usage, "input", result, chat ? "prompt_tokens" : "input_tokens"); Copy(doc.Usage, "output", result, chat ? "completion_tokens" : "output_tokens");
            var inputDetails = doc.Usage["inputDetails"]?.DeepClone().AsObject() ?? new JsonObject();
            var outputDetails = doc.Usage["outputDetails"]?.DeepClone().AsObject() ?? new JsonObject();
            Copy(doc.Usage, "cached", inputDetails, "cached_tokens"); Copy(doc.Usage, "reasoning", outputDetails, "reasoning_tokens");
            if (inputDetails.Count != 0) result[chat ? "prompt_tokens_details" : "input_tokens_details"] = inputDetails;
            if (outputDetails.Count != 0) result[chat ? "completion_tokens_details" : "output_tokens_details"] = outputDetails;
        }
        if (Count(doc, "createdCache") != 0) Loss(doc, "$usage.cache_creation_input_tokens");
        if (doc.Usage["cacheCreationDetails"] is JsonObject creation && creation.Any(f => Number(f.Value) != 0)) Loss(doc, "$usage.cache_creation");
        var total = doc.Usage["total"]?.DeepClone();
        if (total is null && doc.Usage.ContainsKey("input") && doc.Usage.ContainsKey("output")) total = JsonValue.Create(Count(doc, "input") + Count(doc, "output"));
        if (total is not null) result[protocol == LlmProtocol.Gemini ? "totalTokenCount" : "total_tokens"] = total;
        return result;
    }

    /// <summary>已知无法表示的额外明细仅在非零时产生损耗；未知字段仍由 Check 拒绝。</summary>
    private static void DiagnoseDetails(ConversionDocument doc, string key, string represented)
    {
        if (doc.Usage[key] is not JsonObject details) return;
        foreach (var detail in details)
            if (detail.Key != represented && Number(detail.Value) != 0) Loss(doc, "$usage." + key + "." + detail.Key);
    }
    /// <summary>读取累计计数，未提供的计数仅用于口径运算中的零项。</summary>
    private static long Count(ConversionDocument doc, string key) => Number(doc.Usage[key]);
    /// <summary>即使目标保留细分计数，也不能把负数或非整数明细当成有效用量。</summary>
    private static void ValidateDetails(JsonObject details)
    {
        foreach (var detail in details)
            if (detail.Value is JsonValue && Number(detail.Value) < 0) throw new InvalidDataException("Token 明细计数不能为负数。");
    }
}
