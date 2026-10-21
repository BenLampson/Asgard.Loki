using System.Text.Json;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>单次转录流的有界分段引用校验；不保存/拼接历史文本、修改原生值或复用会话状态。</summary>
internal sealed class OpenAiTranscriptionState(int maximum)
{
    private readonly Dictionary<string, bool> _segments = new(StringComparer.Ordinal);

    /// <summary>仅解释已声明的原生事件，未知事件与字段由外层原样交付。</summary>
    internal bool Validate(JsonElement data, string type)
    {
        switch (type)
        {
            case "transcript.text.delta":
                _ = Text(data, "delta"); Logprobs(data);
                if (data.TryGetProperty("segment_id", out var reference) && reference.ValueKind != JsonValueKind.Null)
                {
                    var id = Identity(reference); Track(id);
                    if (_segments[id]) throw new InvalidDataException("转录分段完成后仍出现其文本增量。");
                }
                break;
            case "transcript.text.segment":
                var segment = Identity(Required(data, "id")); Track(segment);
                if (_segments[segment]) throw new InvalidDataException("转录分段 ID 不唯一。");
                _ = Text(data, "text"); _ = Text(data, "speaker");
                var start = Required(data, "start"); var end = Required(data, "end"); _ = Number(start); _ = Number(end);
                if (TranscriptionJsonNumber.Sign(start) < 0 || TranscriptionJsonNumber.Compare(end, start) < 0) throw new InvalidDataException("转录分段时间范围无效。");
                _segments[segment] = true;
                break;
            case "transcript.text.done":
                _ = Text(data, "text"); Logprobs(data); Languages(data); Usage(data);
                if (_segments.Values.Any(completed => !completed)) throw new InvalidDataException("转录完成时仍有未闭合分段引用。");
                return true;
        }
        return false;
    }

    /// <summary>保留已完成 ID 以拒绝重开，超过配置上限时不截断或遗忘关联。</summary>
    private void Track(string id)
    {
        if (_segments.ContainsKey(id)) return;
        if (_segments.Count >= maximum) throw new InvalidDataException("转录分段跟踪数量超过配置上限。");
        _segments.Add(id, false);
    }
    /// <summary>原生引用必须有非空字符串身份，不把 null/数字或空值变成同一个 ID。</summary>
    private static string Identity(JsonElement value)
        => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("原生转录分段身份必须为非空字符串。");
    /// <summary>必需文本可以为空；最终文本不被增量拼接替换或强行对齐。</summary>
    private static string Text(JsonElement data, string name)
        => Required(data, name).ValueKind == JsonValueKind.String ? data.GetProperty(name).GetString()!
            : throw new InvalidDataException("原生转录文本字段类型无效。");
    /// <summary>缺项明确失败，不虚构补齐原生字段。</summary>
    private static JsonElement Required(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) ? value : throw new InvalidDataException("原生转录事件缺少必需字段。");
    /// <summary>已知计量/时间字段只检验有限数值，不改变 JSON 数值、精度或范围端点。</summary>
    private static double Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException("原生转录数值必须为有限 JSON 数字。");
    /// <summary>可选字段的省略/null 均保持原生；有值时校验容器，不删除未知成员。</summary>
    private static bool Optional(JsonElement data, string name, JsonValueKind kind, out JsonElement value)
    {
        if (!data.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != kind) throw new InvalidDataException("原生转录可选字段容器无效。");
        return true;
    }
    /// <summary>保留所有 token/字节/概率，不把 token 字面量当文本重新拼接。</summary>
    private static void Logprobs(JsonElement data)
    {
        if (!Optional(data, "logprobs", JsonValueKind.Array, out var values)) return;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生 logprobs 项必须为对象。");
            _ = Optional(item, "token", JsonValueKind.String, out _);
            if (Optional(item, "logprob", JsonValueKind.Number, out var probability)) _ = Number(probability);
            if (Optional(item, "bytes", JsonValueKind.Array, out var bytes)) foreach (var value in bytes.EnumerateArray())
            { _ = Number(value); if (!TranscriptionJsonNumber.IsByte(value)) throw new InvalidDataException("原生 token bytes 必须为字节数值。"); }
        }
    }
    /// <summary>检测语言保留原顺序和扩展信息，不使用语言目录或推断授权。</summary>
    private static void Languages(JsonElement data)
    {
        if (!Optional(data, "languages", JsonValueKind.Array, out var values)) return;
        foreach (var item in values.EnumerateArray())
        { if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("检测语言必须为原生对象。"); _ = Identity(Required(item, "code")); }
    }
    /// <summary>已知 tokens 口径只检查原生计数类型；未来口径保留为未知对象，不重算计费或总数。</summary>
    private static void Usage(JsonElement data)
    {
        if (!Optional(data, "usage", JsonValueKind.Object, out var value)) return;
        if (!value.TryGetProperty("type", out var type)) return;
        if (type.ValueKind != JsonValueKind.String) throw new InvalidDataException("原生用量 type 必须为字符串。");
        if (type.GetString() != "tokens") return;
        foreach (var field in new[] { "input_tokens", "output_tokens", "total_tokens" })
        { var count = Required(value, field); _ = Number(count); if (TranscriptionJsonNumber.Sign(count) < 0) throw new InvalidDataException("原生 token 计数不能为负数。"); }
        if (Optional(value, "input_token_details", JsonValueKind.Object, out var details)) foreach (var field in new[] { "audio_tokens", "text_tokens" })
            if (Optional(details, field, JsonValueKind.Number, out var count))
            { _ = Number(count); if (TranscriptionJsonNumber.Sign(count) < 0) throw new InvalidDataException("原生 token 明细不能为负数。"); }
    }
}
