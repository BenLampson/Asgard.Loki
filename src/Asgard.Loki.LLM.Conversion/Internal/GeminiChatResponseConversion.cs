using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>完整 Gemini 候选逐个映射为 Chat choices，不共用工具、引用或终态。</summary>
internal static class GeminiChatResponseConversion
{
    /// <summary>共享根级信封与用量，每个候选复用既有单候选语义校验。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmConversionOptions options)
    {
        if (source["error"] is not null)
            throw new LlmConversionException([new("upstream_error", "$.error", "错误响应不能转换为成功响应。")]);
        var candidates = Array(source["candidates"]);
        if (candidates.Count == 0)
            throw new LlmConversionException([new("missing_candidates", "$.candidates", "没有候选的响应不能转换为成功结果。")]);
        var indices = new HashSet<int>();
        foreach (var node in candidates)
            if (!indices.Add(Index(Object(node)))) throw new InvalidDataException("完整 Gemini 候选索引不能重复。");
        // 先移除整个候选数组再复制根级字段，避免每个候选复制所有其他候选。
        var envelope = new JsonObject();
        foreach (var field in source)
            if (field.Key != "candidates") envelope[field.Key] = field.Value?.DeepClone();
        var mapGrounding = options.OpenAiCitationIndexUnit != LlmCitationIndexUnit.Unspecified;
        var aggregate = new ConversionDocument(); JsonObject? target = null;
        for (var position = 0; position < candidates.Count; position++)
        {
            var candidate = Object(candidates[position]);
            envelope["candidates"] = new JsonArray(candidate.DeepClone());
            ConversionDocument doc; JsonObject mapped;
            try
            {
                doc = ResponseMapping.Read(envelope, LlmProtocol.Gemini,
                    citationIndexUnit: options.OpenAiCitationIndexUnit, mapGeminiGrounding: mapGrounding);
                doc.Diagnostics.AddRange(HostedSearchOutput.Inspect(envelope, LlmProtocol.Gemini,
                    mapUrlCitations: true, mapGeminiGrounding: mapGrounding));
                mapped = ResponseMapping.Write(doc, LlmProtocol.ChatCompletions);
            }
            catch (LlmConversionException error)
            {
                throw new LlmConversionException(error.Diagnostics.Select(d => Rebase(d, position)));
            }
            var choice = Object(Array(mapped["choices"])[0]); choice["index"] = Index(candidate);
            if (target is null) target = mapped;
            else Array(target["choices"]).Add(choice.DeepClone());
            aggregate.Diagnostics.AddRange(doc.Diagnostics.Select(d => Rebase(d, position)));
            // 后续候选仅贡献 choice；根级计数与生成的根级 ID 只产生一次。
            envelope.Remove("usageMetadata"); envelope["responseId"] = doc.Id;
        }
        var diagnostics = aggregate.Diagnostics.Distinct().ToArray();
        aggregate.Diagnostics.Clear(); aggregate.Diagnostics.AddRange(diagnostics);
        LlmProtocolConverter.Enforce(aggregate, options);
        return new(target!, source, diagnostics);
    }

    /// <summary>原生非 optional int32 省略为零，显式 null 或非整数不得改成零。</summary>
    private static int Index(JsonObject candidate)
    {
        if (!candidate.ContainsKey("index")) return 0;
        var element = JsonSerializer.SerializeToElement(candidate["index"]);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
            throw new InvalidDataException("Gemini 候选索引必须为非负 int32 数值。");
        return value;
    }

    /// <summary>诊断沿用原始数组位置，不用稀疏原生索引冒充 JSON 数组下标。</summary>
    private static LlmConversionDiagnostic Rebase(LlmConversionDiagnostic diagnostic, int position)
    {
        var path = diagnostic.Path; var candidate = "$.candidates[" + position + "]";
        if (path.StartsWith("$.candidates[0]", StringComparison.Ordinal)) path = candidate + path[15..];
        else if (path.StartsWith("$parts", StringComparison.Ordinal)) path = candidate + ".content.parts" + path[6..];
        else if (path.StartsWith("$content", StringComparison.Ordinal)) path = candidate + ".content.parts" + path[8..];
        else if (path.StartsWith("$response.content", StringComparison.Ordinal)) path = candidate + ".content" + path[17..];
        else if (path == "$finish") path = candidate + ".finishReason";
        return diagnostic with { Path = path };
    }
}
