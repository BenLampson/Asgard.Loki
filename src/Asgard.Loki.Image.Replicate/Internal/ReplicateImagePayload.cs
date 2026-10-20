using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Replicate.Internal;

/// <summary>完整原生 input/根参数快照，不从尺寸、质量、数量或模型名生成设置。</summary>
internal static class ReplicateImagePayload
{
    /// <summary>公共便捷入口的扩展字段是原生 input 字段，根参数使用显式原生请求。</summary>
    internal static ReplicateNativeImageRequest Bridge(string model, string prompt, Dictionary<string, JsonElement> fields, bool edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(prompt); ArgumentNullException.ThrowIfNull(fields);
        ReplicateImageJson.Text(model); ReplicateImageJson.Text(prompt); var input = new JsonObject { ["prompt"] = prompt };
        foreach (var field in fields)
        {
            ReplicateImageJson.Text(field.Key); ReplicateImageJson.Validate(field.Value);
            if (new[] { "prompt", "model", "input" }.Contains(field.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("公共扩展参数不能覆盖 prompt、模型或嵌套另一层 input。");
            input.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        if (edit && (input["image_prompt"] is not JsonValue image || !image.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("此原生编辑输入需要明确的 image_prompt 引用。");
        return new() { PublicModel = model, Parameters = new() { ["input"] = input } };
    }
    /// <summary>在创建 HTTP 资源前冻结所有根字段；本操作不消费返回的 stream URL。</summary>
    internal static byte[] Prepare(ReplicateNativeImageRequest request, ReplicateImageOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicModel);
        ReplicateImageJson.Text(request.PublicModel); ArgumentNullException.ThrowIfNull(request.Parameters); Node(request.Parameters);
        if (request.Parameters["input"] is not JsonObject) throw new ArgumentException("Replicate 预测需要完整原生 input 对象。");
        foreach (var field in request.Parameters)
        {
            if (new[] { "model", "version" }.Contains(field.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("固定模型资源预测不接受模型/版本覆盖。");
            if (field.Key.Equals("input", StringComparison.OrdinalIgnoreCase) && field.Key != "input") throw new ArgumentException("input 不能使用大小写别名。");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Parameters); _ = ReplicateImageJson.Parse(bytes);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Replicate 图片预测请求超过本地字节上限。");
        return bytes;
    }
    /// <summary>避免 CLR 字符串的无效代理项被 JSON 编码器有损替换。</summary>
    private static void Node(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var field in obj) { ReplicateImageJson.Text(field.Key); Node(field.Value); }
        else if (node is JsonArray array) foreach (var item in array) Node(item);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text)) ReplicateImageJson.Text(text);
    }
}
