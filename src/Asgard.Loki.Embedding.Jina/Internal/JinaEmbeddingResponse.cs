using System.Text.Json;

namespace Asgard.Loki.Embedding.Jina.Internal;

/// <summary>验证原生有索引的单/多向量与用量，不 flatten、重排或补造 token 关联。</summary>
internal static class JinaEmbeddingResponse
{
    /// <summary>保留 Jina detail/code 原生错误，即使 HTTP 200 或同时带有 data 也不能伪装成功。</summary>
    internal static bool IsError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "error", "detail", "code" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length != 0)) return true;
        return false;
    }

    /// <summary>数据数量、唯一索引、全批维度/编码/单多外壳均一致；未知字段与原始顺序保留。</summary>
    internal static JsonElement[] Read(JsonElement root, JinaEmbeddingPayload? payload = null)
    {
        if (root.ValueKind != JsonValueKind.Object || IsError(root) || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString())
            || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_tokens", out var total) || !JinaEmbeddingNumber.IsNonNegativeInteger(total)
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Jina 响应缺少原生模型、用量或 data 数组。");
        if (root.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "list"))
            throw new InvalidDataException("原生响应 object 必须为 list。");
        foreach (var name in new[] { "prompt_tokens", "image_tokens", "audio_tokens", "video_tokens", "input_tokens", "output_tokens" })
            if (usage.TryGetProperty(name, out var value)
                && !(value.ValueKind == JsonValueKind.Null && name is "image_tokens" or "audio_tokens" or "video_tokens")
                && !JinaEmbeddingNumber.IsNonNegativeInteger(value)) throw new InvalidDataException("原生 token 计数类型无效。");
        var result = data.EnumerateArray().ToArray();
        if (payload is not null && result.Length != payload.Count) throw new InvalidDataException("Jina 返回数量与完整原生输入项不一致。");
        var seen = new HashSet<int>(); int? width = payload?.Dimensions; JsonValueKind? encoding = null; bool? multi = payload?.Multi;
        foreach (var item in result)
        {
            var state = JinaEmbeddingItemCodec.Read(item);
            if (state.Index >= result.Length || !seen.Add(state.Index)) throw new InvalidDataException("Jina 原生索引重复、缺失或越界。");
            if (multi is not null && multi != state.Multi) throw new InvalidDataException("单/多向量外壳与明确请求或同批外壳不一致。");
            multi ??= state.Multi;
            if (payload?.RequireTokens == true && (!item.TryGetProperty("tokenized_input", out var tokens) || tokens.ValueKind != JsonValueKind.Array))
                throw new InvalidDataException("上游遗漏明确要求的 token 列表。");
            if (state.Width is not null)
            {
                if (width is not null && width != state.Width || encoding is not null && encoding != state.Encoding)
                    throw new InvalidDataException("原生向量维度或编码在同批中不一致。");
                if (payload?.Encoding is { } expected && (expected == "float") != (state.Encoding == JsonValueKind.Array))
                    throw new InvalidDataException("原生向量编码与 embedding_type 不一致。");
                width = state.Width; encoding = state.Encoding;
            }
        }
        return result;
    }
}
