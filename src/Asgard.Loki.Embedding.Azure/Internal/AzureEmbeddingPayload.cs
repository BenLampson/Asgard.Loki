using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.Azure.Internal;

/// <summary>原生输入边界与完整参数快照，不分词、拆批、截断或注入编码/维度默认值。</summary>
internal static class AzureEmbeddingPayload
{
    /// <summary>在 HTTP 工厂及首次等待前捕获全部原生字段和本次响应关联约束。</summary>
    internal static (byte[] Body, int Count, int? Dimensions, string? Encoding) Prepare(EmbeddingRequest request, AzureEmbeddingOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters); AzureEmbeddingJson.Text(request.Model); var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in request.Parameters)
        {
            AzureEmbeddingJson.Text(field.Key); AzureEmbeddingJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Azure 原生 body model 只能由固定目标显式配置。");
            if (new[] { "input", "dimensions", "encoding_format" }.Any(name => name.Equals(field.Key, StringComparison.OrdinalIgnoreCase) && name != field.Key)) throw new ArgumentException("原生嵌入字段不能使用大小写别名。");
            values.Add(field.Key, field.Value.Clone());
        }
        if (!values.TryGetValue("input", out var input)) throw new ArgumentException("Azure 嵌入需要原生 input。");
        var count = Count(input); int? dimensions = null; string? encoding = null;
        if (values.TryGetValue("dimensions", out var size))
        {
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var width) || width <= 0) throw new ArgumentException("dimensions 必须为正整数。");
            dimensions = width;
        }
        if (values.TryGetValue("encoding_format", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || format.GetString() is not ("float" or "base64")) throw new ArgumentException("encoding_format 必须是原生 float 或 base64。");
            encoding = format.GetString();
        }
        var body = new JsonObject(); foreach (var field in values) body.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        if (options.BodyModel is { } model) body.Add("model", model);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body); _ = AzureEmbeddingJson.Parse(bytes);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Azure 嵌入请求超过本地 UTF-8 字节上限。");
        return (bytes, count, dimensions, encoding);
    }
    /// <summary>一个 token 序列是一项输入，文本数组和 token 序列数组按原始边界计数。</summary>
    private static int Count(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String) { Text(input); return 1; }
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0) throw new ArgumentException("原生 input 必须为非空文本或数组。");
        var kind = input[0].ValueKind;
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != kind) throw new ArgumentException("原生 input 不能混用文本、token 和 token 序列。");
            if (kind == JsonValueKind.String) Text(item);
            else if (kind == JsonValueKind.Number) Token(item);
            else if (kind == JsonValueKind.Array)
            {
                if (item.GetArrayLength() == 0) throw new ArgumentException("token 序列不能为空。");
                foreach (var token in item.EnumerateArray()) Token(token);
            }
            else throw new ArgumentException("原生 input 数组结构无效。");
        }
        return kind == JsonValueKind.Number ? 1 : input.GetArrayLength();
    }
    private static void Text(JsonElement value)
    { if (value.GetString() is not { Length: > 0 }) throw new ArgumentException("嵌入文本不能为空；原始空格不修剪。"); }
    private static void Token(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var token) || token < 0) throw new ArgumentException("token ID 必须为非负整数。"); }
}
