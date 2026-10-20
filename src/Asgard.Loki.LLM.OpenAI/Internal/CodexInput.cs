using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生 JSON 快照，不删除供应商参数或补 instructions/store/工具/推理默认值。</summary>
internal static class CodexInput
{
    /// <summary>首次来源/网络等待前检查原始 CLR 和嵌入元素，再序列化完整独立正文。</summary>
    internal static byte[] Body(JsonObject payload, string? model, bool? stream, int maximum, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(payload); var pending = new Stack<JsonNode?>(); pending.Push(payload);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { OpenAiHttpJson.Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<JsonElement>(out var element)) _ = OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(element.GetRawText()));
                else if (value.TryGetValue<string>(out var text)) OpenAiHttpJson.Text(text);
            }
        }
        foreach (var property in payload)
        {
            if (model is not null && property.Key.Equals("model", StringComparison.OrdinalIgnoreCase) && property.Key != "model"
                || (stream.HasValue || compact) && property.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) && (property.Key != "stream" || compact))
                throw new ArgumentException("Codex 原生 model/stream 别名冲突或 compact 使用了不支持的流参数。");
        }
        var body = payload.DeepClone().AsObject(); if (model is not null) body["model"] = model; if (stream.HasValue) body["stream"] = stream.Value;
        var textBody = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(textBody) > maximum) throw new InvalidDataException("Codex 原生请求超过 UTF-8 字节预算。");
        return utf8.GetBytes(textBody);
    }
    /// <summary>只追加原生操作，不猜测地区、模型、账户或 API 根。</summary>
    internal static Uri Endpoint(Uri root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Codex 需要无凭据、查询或片段的完整 HTTP(S) API 根。");
        return new(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path);
    }
}
