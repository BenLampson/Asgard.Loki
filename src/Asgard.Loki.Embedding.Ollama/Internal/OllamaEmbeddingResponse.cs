using System.Text.Json;

namespace Asgard.Loki.Embedding.Ollama.Internal;

/// <summary>验证原生有序向量、对应数量和可选计数，不构造索引或 OpenAI 用量。</summary>
internal static class OllamaEmbeddingResponse
{
    /// <summary>缺省计数保持未知；零向量只在零输入或独立读取原生结果时有效。</summary>
    internal static JsonElement[] Read(JsonElement root, int? count = null, long? dimensions = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()))
            throw new InvalidDataException("Ollama 嵌入响应缺少原生模型名。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("业务错误不能作为成功嵌入读取。");
        if (!root.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("原生响应需要 embeddings 数组。");
        var result = embeddings.EnumerateArray().ToArray();
        if (count is not null && count != result.Length) throw new InvalidDataException("返回向量数量与原生输入项数量不一致。");
        long? size = dimensions;
        foreach (var vector in result)
        {
            var actual = Validate(vector); size ??= actual;
            if (size != actual) throw new InvalidDataException("向量维度与明确请求或同批维度不一致。");
        }
        foreach (var name in new[] { "total_duration", "load_duration", "prompt_eval_count" })
            if (root.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0))
                throw new InvalidDataException("原生时长与 token 计数必须是非负 Int64 数值。");
        return result;
    }

    /// <summary>只接受非空有限数值数组，不解码字符串、归一化、截断或填补维度。</summary>
    internal static int Validate(JsonElement vector)
    {
        if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() == 0) throw new InvalidDataException("每项嵌入必须为非空数值数组。");
        foreach (var value in vector.EnumerateArray())
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException("嵌入向量只接受有限 JSON 数值。");
        return vector.GetArrayLength();
    }
}
