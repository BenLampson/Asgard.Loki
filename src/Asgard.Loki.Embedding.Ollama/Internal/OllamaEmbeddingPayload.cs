using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.Ollama.Internal;

/// <summary>保留 Ollama 原生输入及参数，仅计算对应数量，不改写空值、媒体或默认设置。</summary>
internal static class OllamaEmbeddingPayload
{
    /// <summary>零输入的显式原生请求允许零向量；批内空字符串仍独立占一项。</summary>
    internal static (JsonObject Body, int Count, long? Dimensions) Create(EmbeddingRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生参数不能覆盖公共 model 或含 Undefined。");
        var values = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        var count = values.TryGetValue("input", out var input) ? Count(input) : 0;
        long? dimensions = null;
        if (values.TryGetValue("dimensions", out var size) && size.ValueKind != JsonValueKind.Null)
        {
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var number)) throw new ArgumentException("dimensions 必须为原生整数或 null。");
            if (number > 0) dimensions = number;
        }
        if (values.TryGetValue("truncate", out var truncate) && truncate.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("truncate 必须为布尔值或 null。");
        if (values.TryGetValue("options", out var options) && options.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new ArgumentException("options 必须为原生对象或 null。");
        if (values.TryGetValue("keep_alive", out var keepAlive) && keepAlive.ValueKind != JsonValueKind.Null
            && (keepAlive.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
                || keepAlive.ValueKind == JsonValueKind.Number && (!keepAlive.TryGetDouble(out var seconds) || !double.IsFinite(seconds))))
            throw new ArgumentException("keep_alive 必须为原生时长字符串、有限秒数或 null。");
        var body = JsonSerializer.SerializeToNode(values)!.AsObject(); body["model"] = model;
        return (body, count, dimensions);
    }

    /// <summary>输入容器遵循当前原生 string/object/array 方言，不将 token 数组误认作文本批次。</summary>
    private static int Count(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Null) return 0;
        if (input.ValueKind == JsonValueKind.String) return input.GetString()!.Length == 0 ? 0 : 1;
        if (input.ValueKind == JsonValueKind.Object) { Item(input); return 1; }
        if (input.ValueKind != JsonValueKind.Array) throw new ArgumentException("input 必须为原生文本、媒体对象、批数组或 null。");
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) Item(item);
            else if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("批 input 只接受文本或原生媒体对象。");
        }
        return input.GetArrayLength();
    }

    /// <summary>校验已知媒体字段为原生 Base64，不推测 MIME、下载链接或解释媒体内容；未知字段仍保留。</summary>
    private static void Item(JsonElement item)
    {
        if (item.TryGetProperty("video", out _)) throw new ArgumentException("当前原生嵌入不支持 video 输入。");
        var content = false;
        foreach (var field in item.EnumerateObject())
        {
            if (field.Name is not ("text" or "image" or "audio")) continue;
            if (field.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("原生 input 的 text/image/audio 必须是字符串。");
            var text = field.Value.GetString()!;
            if (field.Name == "text") content |= text.Length != 0;
            else
            {
                if (text.Any(c => char.IsWhiteSpace(c) && c is not ('\r' or '\n'))) throw new ArgumentException("媒体必须为原生标准 Base64。");
                try { _ = Convert.FromBase64String(text); }
                catch (FormatException error) { throw new ArgumentException("媒体必须为调用者提供的原生标准 Base64。", error); }
                content = true;
            }
        }
        if (!content) throw new ArgumentException("input 对象缺少非空文本或媒体字段。");
    }
}
