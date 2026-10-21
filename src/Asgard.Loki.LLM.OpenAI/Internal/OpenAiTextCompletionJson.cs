using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>只验证文本补全已知载体，不映射内容、计分、关联索引、结束原因或未知属性。</summary>
internal static class OpenAiTextCompletionJson
{
    /// <summary>完整文本结果必须包含有内容的原生选择项，不将 Chat message/delta 当作 text。</summary>
    internal static void Complete(JsonElement data)
    {
        if (!data.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) throw new InvalidDataException("文本补全完整结果需要原生 choices。");
        var indices = new HashSet<long>();
        foreach (var choice in choices.EnumerateArray())
        { if (!indices.Add(Index(choice))) throw new InvalidDataException("文本补全完整结果的索引重复。"); _ = Generation(choice); if (!choice.TryGetProperty("text", out _)) throw new InvalidDataException("文本补全完整结果缺少原生 text。"); Finish(choice, true); }
    }
    /// <summary>索引保持精确非负整数；只用于字典键，不用于分配数组或重新编号。</summary>
    internal static long Index(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt64(out var value) || value < 0) throw new InvalidDataException("文本补全 choice 缺少有效原生索引。"); return value;
    }
    /// <summary>text 或非空 logprobs 才是已知生成载体；空载体和未知扩展不补造内容。</summary>
    internal static bool Generation(JsonElement choice)
    {
        var generated = false;
        if (!choice.TryGetProperty("text", out _) && (choice.TryGetProperty("delta", out _) || choice.TryGetProperty("message", out _))) throw new InvalidDataException("文本补全不接受 Chat 选择项代替原生 text。");
        if (choice.TryGetProperty("text", out var text)) { if (text.ValueKind != JsonValueKind.String) throw new InvalidDataException("文本补全 text 必须为原生字符串。"); generated = text.GetString()!.Length != 0; }
        if (choice.TryGetProperty("logprobs", out var logs) && logs.ValueKind != JsonValueKind.Null)
        {
            if (logs.ValueKind != JsonValueKind.Object) throw new InvalidDataException("文本补全 logprobs 必须为原生对象或 null。");
            foreach (var name in new[] { "tokens", "token_logprobs", "top_logprobs", "text_offset" })
                if (logs.TryGetProperty(name, out var values) && values.ValueKind != JsonValueKind.Null)
                { if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("文本补全已知 logprobs 载体必须为数组。"); generated |= values.GetArrayLength() != 0; }
        }
        return generated;
    }
    /// <summary>结束原因只保留原生非空字符串，未知的新字符串不被改成 stop。</summary>
    internal static bool Finish(JsonElement choice, bool required = false)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason) || reason.ValueKind == JsonValueKind.Null)
        { if (required) throw new InvalidDataException("完整文本补全缺少原生结束原因。"); return false; }
        if (reason.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reason.GetString())) throw new InvalidDataException("文本补全原生结束原因无效。"); return true;
    }
}
