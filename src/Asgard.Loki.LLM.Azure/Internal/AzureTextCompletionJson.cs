using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>原生 prompt/text 形状与完整结果校验，不套消息模板或重排 choice/logprobs。</summary>
internal static class AzureTextCompletionJson
{
    /// <summary>冻结原生 prompt 参数，只写实际部署与方法流式标志，按最终 UTF-8 字节限制。</summary>
    internal static byte[] Request(JsonObject payload, LlmAdapterContext context, bool stream, bool dated, int maximum)
    {
        AzureJson.Input(payload);
        if (payload.Any(p => (p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) && p.Key != "model") || (p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) && p.Key != "stream"))) throw new ArgumentException("文本补全不接受模型或流式字段别名。");
        var body = payload.DeepClone().AsObject(); if (dated) body.Remove("model"); else body["model"] = context.Target.UpstreamModel; body["stream"] = stream;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body); if (bytes.Length > maximum) throw new ArgumentException("Azure 文本补全请求超过最终 UTF-8 字节上限。"); var raw = AzureJson.Parse(bytes);
        if (raw.TryGetProperty("prompt", out var prompt) && prompt.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            if (prompt.ValueKind != JsonValueKind.Array) throw new ArgumentException("原生 prompt 必须为文本、token、批次或空值。");
            var items = prompt.EnumerateArray().ToArray();
            if (!items.All(v => v.ValueKind == JsonValueKind.String) && !items.All(Token) && !items.All(v => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(Token))) throw new ArgumentException("原生 prompt 批次不能混合文本、token 或不同层级。");
        }
        return bytes;
    }
    /// <summary>识别文档化的根错误格式，不将正常 choices 中的业务字段视为错误。</summary>
    internal static bool Error(JsonElement data) => AzureJson.IsError(data) || data.ValueKind == JsonValueKind.Object && !data.TryGetProperty("choices", out _) && data.TryGetProperty("code", out _) && data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String;
    /// <summary>检查完整原生结构，保留实际索引顺序及全部未知数据。</summary>
    internal static void Complete(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("object", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "text_completion" || !data.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Azure 没有返回完整原生 text_completion。");
        foreach (var name in new[] { "id", "model" }) if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException("原生文本补全缺少结果身份。");
        if (!data.TryGetProperty("created", out var created) || created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out var timestamp) || timestamp < 0) throw new InvalidDataException("原生文本补全创建时间无效。");
        var indices = new HashSet<int>();
        foreach (var choice in choices.EnumerateArray())
        {
            var index = Index(choice); if (!indices.Add(index)) throw new InvalidDataException("原生文本补全 choice 索引重复。");
            if (!choice.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || !choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(finish.GetString())) throw new InvalidDataException("完整文本补全 choice 缺少原生文本或结束原因。");
            _ = Generation(choice);
        }
    }
    /// <summary>索引保持原值，不允许负数、非整数或超出协议整数范围。</summary>
    internal static int Index(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var index) || index < 0) throw new InvalidDataException("原生文本补全 choice 索引无效。"); return index;
    }
    /// <summary>只检查原生 text 与已知 logprobs 序列是否含生成数据。</summary>
    internal static bool Generation(JsonElement choice)
    {
        var generation = false;
        if (choice.TryGetProperty("text", out var text) && text.ValueKind != JsonValueKind.Null)
        { if (text.ValueKind != JsonValueKind.String) throw new InvalidDataException("原生 choice text 必须为文本或 null。"); generation = text.GetString()!.Length != 0; }
        if (choice.TryGetProperty("logprobs", out var probabilities) && probabilities.ValueKind != JsonValueKind.Null)
        {
            if (probabilities.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生 logprobs 必须为对象或 null。");
            foreach (var name in new[] { "tokens", "token_logprobs", "top_logprobs", "text_offset" })
                if (probabilities.TryGetProperty(name, out var array) && array.ValueKind != JsonValueKind.Null)
                { if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生 logprobs 序列必须为数组或 null。"); generation |= array.GetArrayLength() != 0; }
        }
        return generation;
    }
    /// <summary>原生 token 保持非负整数，不推断分词器或修复不合法 token。</summary>
    private static bool Token(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var token) && token >= 0;
}
