using System.Text.Json;

namespace Asgard.Loki.LLM.Google.Internal;

/// <summary>只维护原生候选与响应身份，不拼接工具、不重写候选或制造 STOP。</summary>
internal sealed class GoogleStreamState(int maximumCandidates)
{
    private readonly HashSet<long> _seen = [];
    private readonly HashSet<long> _finished = [];
    private string? _responseId;
    private string? _modelVersion;
    internal bool Finishing { get; private set; }
    internal bool Fatal { get; private set; }
    /// <summary>没有原生候选、用量或反馈载体的未知 type 仅为业务元数据；扩展字段不屏蔽已知载体。</summary>
    private static bool Unknown(JsonElement data) => data.TryGetProperty("type", out var type)
        && !(type.ValueKind == JsonValueKind.String && type.GetString() == "error")
        && !data.TryGetProperty("candidates", out _) && !data.TryGetProperty("usageMetadata", out _) && !data.TryGetProperty("promptFeedback", out _);
    /// <summary>只识别原生根或明确错误事件，不遍历未知业务对象。</summary>
    private static bool Failure(JsonElement data, string? eventName)
        => eventName == "error" || !Unknown(data) && (data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "error" || data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null);
    /// <summary>阻断反馈必须是明确、非默认的原生 blockReason。</summary>
    private static bool Block(JsonElement data) => !Unknown(data) && data.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object && feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String && block.GetString() is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED";
    internal bool LateFailure(JsonElement data, string? eventName) => Finishing && !Fatal && (Failure(data, eventName) || Block(data));
    /// <summary>每个已观察候选必须原生结束；未知元数据保持原文且不参与响应身份关联。</summary>
    internal void Observe(JsonElement data, string? eventName)
    {
        if (Fatal) throw new InvalidDataException("Gemini 错误或阻断终态之后仍有数据。");
        if (Unknown(data) && eventName != "error") return;
        Identity(data, "responseId", ref _responseId); Identity(data, "modelVersion", ref _modelVersion);
        if (Failure(data, eventName) || Block(data)) { Fatal = true; Finishing = true; return; }
        if (!data.TryGetProperty("candidates", out var candidates)) return;
        if (candidates.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gemini candidates 必须为数组。");
        var indices = new HashSet<long>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 候选必须为对象。");
            var index = 0L;
            if (candidate.TryGetProperty("index", out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out index))) throw new InvalidDataException("Gemini 候选索引必须为整数。");
            if (index < 0 || !indices.Add(index)) throw new InvalidDataException("Gemini 候选索引为负数或同帧重复。");
            if (!_seen.Contains(index) && _seen.Count >= maximumCandidates) throw new InvalidDataException("Gemini 候选关联数超过上限。");
            var final = candidate.TryGetProperty("finishReason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is { Length: > 0 } reason && reason != "FINISH_REASON_UNSPECIFIED";
            var generated = candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() != 0;
            if (_finished.Contains(index) && (final || generated)) throw new InvalidDataException("Gemini 候选已结束后再次生成或结束。");
            _seen.Add(index); if (final) { _finished.Add(index); Finishing = true; }
        }
    }
    /// <summary>EOF 不代替任何已观察到的候选结束，也不制造空候选。</summary>
    internal void Complete() { if (!Fatal && (_seen.Count == 0 || !_seen.SetEquals(_finished))) throw new InvalidDataException("Gemini 在全部已观察候选终态前截断。"); }
    /// <summary>存在的原生非空身份必须一致；空值不补造身份，未知业务帧不进入此处。</summary>
    private static void Identity(JsonElement data, string field, ref string? captured)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Gemini 原生响应身份必须为字符串。");
        var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("Gemini 原生响应身份在流中改变。"); captured = text;
    }
}
