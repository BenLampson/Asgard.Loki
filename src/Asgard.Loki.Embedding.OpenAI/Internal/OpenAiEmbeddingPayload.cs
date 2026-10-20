using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.OpenAI.Internal;

/// <summary>校验原生输入结构并映射实际模型，不分词、截断、拆批或增加编码默认值。</summary>
internal static class OpenAiEmbeddingPayload
{
    /// <summary>独立复制扩展字段，直接适配器调用也不保留外部可变字典引用。</summary>
    internal static (JsonObject Body, int Count, int? Dimensions, string? Encoding) Create(EmbeddingRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生嵌入参数不能覆盖公共 model 或包含 Undefined。", nameof(request));
        OpenAiEmbeddingJson.Text(request.Model);
        foreach (var field in request.Parameters) { OpenAiEmbeddingJson.Text(field.Key); OpenAiEmbeddingJson.Validate(field.Value); }
        var parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!parameters.TryGetValue("input", out var input)) throw new ArgumentException("OpenAI 嵌入需要原生 input。");
        var count = InputCount(input); int? dimensions = null; string? encoding = null;
        if (parameters.TryGetValue("dimensions", out var dimension))
        {
            if (dimension.ValueKind != JsonValueKind.Number || !dimension.TryGetInt32(out var size) || size <= 0)
                throw new ArgumentException("dimensions 必须是正整数。");
            dimensions = size;
        }
        if (parameters.TryGetValue("encoding_format", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || format.GetString() is not ("float" or "base64"))
                throw new ArgumentException("encoding_format 必须为 float 或 base64。");
            encoding = format.GetString();
        }
        var payload = JsonSerializer.SerializeToNode(parameters)!.AsObject(); payload["model"] = model;
        return (payload, count, dimensions, encoding);
    }

    /// <summary>区分一个 token 序列和多个文本或 token 序列，保留输入边界与次序。</summary>
    private static int InputCount(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String) { Text(input); return 1; }
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0) throw new ArgumentException("嵌入 input 不能为空或使用未知结构。");
        var kind = input[0].ValueKind;
        foreach (var value in input.EnumerateArray())
        {
            if (value.ValueKind != kind) throw new ArgumentException("input 不能混用文本与 token 序列。");
            if (kind == JsonValueKind.Number) Token(value);
            else if (kind == JsonValueKind.String) Text(value);
            else if (kind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 0) throw new ArgumentException("token 序列不能为空。");
                foreach (var token in value.EnumerateArray()) Token(token);
            }
            else throw new ArgumentException("未知的嵌入输入类型。");
        }
        return kind == JsonValueKind.Number ? 1 : input.GetArrayLength();
    }
    /// <summary>只拒绝原生空文本，不修剪空格或推算模型 token 上限。</summary>
    private static void Text(JsonElement value)
    {
        if (value.GetString() is not { Length: > 0 }) throw new ArgumentException("嵌入文本不能为空。");
    }
    /// <summary>token ID 保持非负整数，不做 tokenizer 转换或字符串修复。</summary>
    private static void Token(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var token) || token < 0)
            throw new ArgumentException("token ID 必须是非负整数。");
    }
}
