using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.Jina.Internal;

/// <summary>快照原生参数和明确输出要求，不增加默认值、模型规则或字段别名。</summary>
internal sealed class JinaEmbeddingPayload(JsonObject body, int count, int? dimensions, string? encoding, bool? multi, bool tokens)
{
    /// <summary>实际原生模型与完整参数。</summary>
    internal JsonObject Body { get; } = body;
    /// <summary>本次完整原生输入对应的结果数。</summary>
    internal int Count { get; } = count;
    /// <summary>显式输出维度，null 不擅自当成零或模型默认值。</summary>
    internal int? Dimensions { get; } = dimensions;
    /// <summary>显式原生 float/base64 要求；缺失与 null 都不猜测响应编码。</summary>
    internal string? Encoding { get; } = encoding;
    /// <summary>显式多向量要求；省略时可接收模型原生的单向量或多向量外壳。</summary>
    internal bool? Multi { get; } = multi;
    /// <summary>是否明确要求对应 token 列表。</summary>
    internal bool RequireTokens { get; } = tokens;

    /// <summary>直接适配器调用同样保护快照，已知不支持的编码不伪装成 float。</summary>
    internal static JinaEmbeddingPayload Create(EmbeddingRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生参数不能覆盖公共 model 或包含 Undefined。");
        var values = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!values.TryGetValue("input", out var input)) throw new ArgumentException("Jina 需要原生 input。");
        var shape = JinaEmbeddingInput.Read(input); int? dimensions = null; string? encoding = null;
        if (values.TryGetValue("dimensions", out var size) && size.ValueKind != JsonValueKind.Null)
        {
            if (!JinaEmbeddingNumber.TryInt32(size, out var number) || number <= 0) throw new ArgumentException("dimensions 必须精确等于正 Int32 或为 null。");
            dimensions = number;
        }
        if (values.TryGetValue("embedding_type", out var type) && type.ValueKind != JsonValueKind.Null)
        {
            if (type.ValueKind == JsonValueKind.Array || type.ValueKind == JsonValueKind.String && type.GetString() is "binary" or "ubinary")
                throw new NotSupportedException("原生 packed/多编码输出尚未实现，不能当作 float 或自动删除设置。");
            if (type.ValueKind != JsonValueKind.String || type.GetString() is not ("float" or "base64")) throw new ArgumentException("embedding_type 必须为当前支持的原生编码或 null。");
            encoding = type.GetString();
        }
        var multi = Boolean(values, "return_multivector"); var tokens = Boolean(values, "return_tokenized_input");
        if (multi == true && dimensions is not null) throw new ArgumentException("显式 return_multivector=true 不能同时指定 dimensions。");
        if (tokens == true && multi != true) throw new ArgumentException("return_tokenized_input=true 需要显式 return_multivector=true。");
        if (Boolean(values, "late_chunking") == true && !shape.TextOnly) throw new ArgumentException("late_chunking=true 只用于文本输入。");
        _ = Boolean(values, "normalized"); _ = Boolean(values, "truncate");
        foreach (var name in new[] { "task", "input_type" })
            if (values.TryGetValue(name, out var setting) && setting.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ArgumentException(name + " 必须为字符串或 null。");
        var body = JsonSerializer.SerializeToNode(values)!.AsObject(); body["model"] = model;
        return new(body, shape.Count, dimensions, encoding, multi, tokens == true);
    }

    /// <summary>可空布尔选项保持原文，不把 null 或缺失改成 false。</summary>
    private static bool? Boolean(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(name + " 必须为布尔值或 null。");
        return value.GetBoolean();
    }
}
