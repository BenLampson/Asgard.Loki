using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>完整 Chat/Gemini 候选逐个映射，不共用工具、引用或终态。</summary>
internal static class CandidateResponseConversion
{
    /// <summary>共享根级信封与用量，每个候选复用既有单候选语义校验。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol from, LlmProtocol to, LlmConversionOptions options)
    {
        if (source["error"] is not null)
            throw new LlmConversionException([new("upstream_error", "$.error", "错误响应不能转换为成功响应。")]);
        var gemini = from == LlmProtocol.Gemini;
        var sourceKey = gemini ? "candidates" : "choices"; var targetKey = gemini ? "choices" : "candidates";
        var usageKey = gemini ? "usageMetadata" : "usage"; var idKey = gemini ? "responseId" : "id";
        var candidates = Array(source[sourceKey]);
        if (candidates.Count == 0)
            throw new LlmConversionException([new("missing_candidates", "$." + sourceKey, "没有候选的响应不能转换为成功结果。")]);
        var indices = new HashSet<int>();
        foreach (var node in candidates)
            if (!indices.Add(Index(Object(node), gemini || candidates.Count == 1))) throw new InvalidDataException("完整响应候选索引不能重复。");
        // 先移除整个候选数组再复制根级字段，避免每个候选复制所有其他候选。
        var envelope = new JsonObject();
        foreach (var field in source)
            if (field.Key != sourceKey) envelope[field.Key] = field.Value?.DeepClone();
        var mapGrounding = gemini && options.OpenAiCitationIndexUnit != LlmCitationIndexUnit.Unspecified;
        var mapGeminiCitations = !gemini && options.OpenAiCitationIndexUnit != LlmCitationIndexUnit.Unspecified;
        var aggregate = new ConversionDocument(); JsonObject? target = null;
        for (var position = 0; position < candidates.Count; position++)
        {
            var candidate = Object(candidates[position]);
            envelope[sourceKey] = new JsonArray(candidate.DeepClone());
            ConversionDocument doc; JsonObject mapped;
            try
            {
                doc = ResponseMapping.Read(envelope, from,
                    citationIndexUnit: options.OpenAiCitationIndexUnit, mapGeminiGrounding: mapGrounding);
                doc.Diagnostics.AddRange(HostedSearchOutput.Inspect(envelope, from,
                    mapUrlCitations: true, mapGeminiGrounding: mapGrounding));
                mapped = ResponseMapping.Write(doc, to, mapGeminiCitations);
            }
            catch (LlmConversionException error)
            {
                throw new LlmConversionException(error.Diagnostics.Select(d => Rebase(d, position, gemini)));
            }
            var choice = Object(Array(mapped[targetKey])[0]); choice["index"] = Index(candidate, gemini || candidates.Count == 1);
            if (target is null) target = mapped;
            else Array(target[targetKey]).Add(choice.DeepClone());
            aggregate.Diagnostics.AddRange(doc.Diagnostics.Select(d => Rebase(d, position, gemini)));
            // 后续候选仅贡献 choice；根级计数与生成的根级 ID 只产生一次。
            envelope.Remove(usageKey); envelope[idKey] = doc.Id;
        }
        var diagnostics = aggregate.Diagnostics.Distinct().ToArray();
        aggregate.Diagnostics.Clear(); aggregate.Diagnostics.AddRange(diagnostics);
        LlmProtocolConverter.Enforce(aggregate, options);
        return new(target!, source, diagnostics);
    }

    /// <summary>Gemini 省略索引为零；单个旧 Chat 结果兼容省略索引，多候选 Chat 必须明确身份。</summary>
    private static int Index(JsonObject candidate, bool allowMissing)
    {
        if (!candidate.ContainsKey("index") && allowMissing) return 0;
        var element = JsonSerializer.SerializeToElement(candidate["index"]);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
            throw new InvalidDataException("候选索引必须为非负 int32 数值。");
        return value;
    }

    /// <summary>诊断沿用原始数组位置，不用稀疏原生索引冒充 JSON 数组下标。</summary>
    private static LlmConversionDiagnostic Rebase(LlmConversionDiagnostic diagnostic, int position, bool gemini)
    {
        var path = diagnostic.Path; var key = gemini ? "candidates" : "choices";
        var first = "$." + key + "[0]"; var candidate = "$." + key + "[" + position + "]";
        var content = candidate + (gemini ? ".content.parts" : ".message.content");
        if (path.StartsWith(first, StringComparison.Ordinal)) path = candidate + path[first.Length..];
        else if (!gemini && path.StartsWith("$.messages[]", StringComparison.Ordinal)) path = candidate + ".message" + path[12..];
        else if (path.StartsWith("$parts", StringComparison.Ordinal)) path = content + path[6..];
        else if (path.StartsWith("$content", StringComparison.Ordinal)) path = content + path[8..];
        else if (path.StartsWith("$response.content", StringComparison.Ordinal)) path = content + path[17..];
        else if (path == "$finish") path = candidate + (gemini ? ".finishReason" : ".finish_reason");
        return diagnostic with { Path = path };
    }
}
