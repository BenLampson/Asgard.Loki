using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>独立验证原生文本批次和多模态内容组，不拼接输入、填默认值或读取媒体。</summary>
internal static class OpenAiModerationPayload
{
    /// <summary>在工厂前冻结完整请求字节和输入关联数量；保留未知字段及显式空值。</summary>
    internal static (byte[] Bytes, int Count) Create(JsonObject request, LlmAdapterContext context, int maximum)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(context); OpenAiHttpJson.Input(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > maximum) throw new ArgumentException("Moderations 请求超过本地 UTF-8 字节上限。");
        var root = OpenAiHttpJson.Parse(bytes);
        if (root.EnumerateObject().Any(p => p.Name.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("model", StringComparison.OrdinalIgnoreCase) && p.Name != "model")) throw new ArgumentException("Moderations 不接受 stream 或模型字段别名。");
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString())
            || !string.Equals(model.GetString(), context.Target.UpstreamModel, StringComparison.Ordinal)) throw new ArgumentException("原生 model 必须匹配已确定目标的实际模型。");
        if (!root.TryGetProperty("input", out var input)) throw new ArgumentException("原生 Moderations 必须包含 input。");
        if (input.ValueKind == JsonValueKind.String) return (bytes, 1);
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0) throw new ArgumentException("input 必须为文本、非空文本批次或多模态内容组。");
        if (input[0].ValueKind == JsonValueKind.String)
        {
            if (input.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw new ArgumentException("文本批次不能混用其他原生类型。");
            return (bytes, input.GetArrayLength());
        }
        foreach (var part in input.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new ArgumentException("多模态内容组缺少原生 type。");
            if (type.GetString() == "text")
            { if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new ArgumentException("text 内容块需要原生字符串。"); }
            else if (type.GetString() == "image_url")
            { if (!part.TryGetProperty("image_url", out var image) || image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) throw new ArgumentException("image_url 内容块需要原生 url 对象。"); }
            else throw new ArgumentException("原生 Moderations 不支持该内容块类型。");
        }
        return (bytes, 1);
    }
}
