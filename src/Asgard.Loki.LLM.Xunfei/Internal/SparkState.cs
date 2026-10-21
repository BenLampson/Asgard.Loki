using System.Text.Json;

namespace Asgard.Loki.LLM.Xunfei.Internal;

/// <summary>原生会话/文本状态分别维护；保留插件和全部 text 项，不把保留 index 变成候选关联。</summary>
internal sealed class SparkState
{
    private string? _sid;
    private long? _sequence;
    private bool _choicesEnded;
    /// <summary>原生全局会话状态已结束。</summary>
    internal bool Ended { get; private set; }
    /// <summary>任一原生结束信号之后的完整消息窗口均须暂存。</summary>
    internal bool Finishing => Ended || _choicesEnded;
    /// <summary>原生非零 code 保留完整字节，不伪造 HTTP 状态。</summary>
    internal static JsonElement Parse(byte[] body)
    {
        var data = SparkJson.Parse(body);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object
            || !header.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value)) throw new InvalidDataException("星火原生消息缺少有效 header.code。");
        if (value != 0) throw new XunfeiSparkUpstreamException(value, body, data); return data;
    }
    /// <summary>插件前缀可先于文本首条状态；header/choices 的结束意义不互相替代。</summary>
    internal void Observe(JsonElement data)
    {
        if (Ended) throw new InvalidDataException("星火全局结束后仍有消息。"); var header = data.GetProperty("header");
        if (!header.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(sid.GetString())) throw new InvalidDataException("星火原生消息缺少 sid。");
        if (_sid is not null && _sid != sid.GetString()) throw new InvalidDataException("星火原生 sid 改变。"); _sid = sid.GetString();
        var status = Status(header);
        if (!data.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("星火原生消息缺少 payload。");
        if (payload.TryGetProperty("choices", out var choices) && choices.ValueKind != JsonValueKind.Null)
        {
            if (choices.ValueKind != JsonValueKind.Object || _choicesEnded) throw new InvalidDataException("星火文本状态无效或已结束。");
            var textStatus = Status(choices);
            if (!choices.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var number) || number < 0 || _sequence is { } previous && number <= previous) throw new InvalidDataException("星火原生 seq 缺失、重复或回退。");
            if (_sequence is not null && textStatus == 0) throw new InvalidDataException("星火文本首条状态重复。"); _sequence = number;
            if (choices.TryGetProperty("text", out var text))
            {
                if (text.ValueKind != JsonValueKind.Array) throw new InvalidDataException("星火 text 必须为数组。");
                foreach (var item in text.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("星火 text 项必须为对象。");
                    foreach (var name in new[] { "role", "content" })
                        if (item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("星火已知文本字段类型无效。");
                }
            }
            _choicesEnded = textStatus == 2;
        }
        Ended = status == 2;
        if (Ended) Complete();
    }
    /// <summary>正常 WebSocket 关闭本身不能补造缺失的原生全局/文本终态。</summary>
    internal void Complete()
    { if (_sid is null || !Ended || !_choicesEnded) throw new InvalidDataException("星火原生会话或文本尚未结束。"); }
    /// <summary>仅接受文档声明的原生整数状态，不把空值或字符串当结束。</summary>
    private static int Status(JsonElement data)
    {
        if (!data.TryGetProperty("status", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var status) || status is < 0 or > 2) throw new InvalidDataException("星火原生 status 无效。"); return status;
    }
}
