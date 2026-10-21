using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>仅在已实现的 Gemini 到 Chat 请求方向传递显式候选数量。</summary>
internal static class GeminiCandidateCount
{
    /// <summary>原生字段不能用字符串、小数或 null 替代，也不猜测供应商模型上限。</summary>
    internal static int? Read(JsonObject request, LlmProtocol from, LlmProtocol to, bool isStream = false, LlmConversionOptions? options = null)
    {
        if (from != LlmProtocol.Gemini || to != LlmProtocol.ChatCompletions
            || request["generationConfig"] is not JsonObject config || !config.ContainsKey("candidateCount")) return null;
        var element = JsonSerializer.SerializeToElement(config["candidateCount"]);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var count) || count <= 0)
            throw new InvalidDataException("Gemini candidateCount 必须是正 int32 数值。");
        if (request.ContainsKey("stream"))
        {
            var stream = JsonSerializer.SerializeToElement(request["stream"]);
            if (stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("stream 必须是布尔值。");
            isStream |= stream.GetBoolean();
        }
        if (isStream && count > 1 && options?.EnableMultipleCandidateStreams != true)
            throw new LlmConversionException([new("multiple_candidate_stream_not_supported", "$.generationConfig.candidateCount", "多候选流式转换尚未实现，不能用单候选状态机调用上游。")]);
        if (isStream && options?.EnableMultipleCandidateStreams == true && count > (options.MaxStreamCandidates ?? 128))
            CandidateStreamBudget.Reject("stream_candidate_limit");
        return count;
    }

    /// <summary>只在已识别字段存在时拍摄副本，其余配置仍经过既有转换与损耗校验。</summary>
    internal static JsonObject Prepare(JsonObject request, int? count)
    {
        if (count is null) return request;
        var copy = request.DeepClone().AsObject(); copy["generationConfig"]!.AsObject().Remove("candidateCount");
        return copy;
    }
}
