using System.Text.Json;

namespace Asgard.Loki.Image.GoogleVertex.Internal;

/// <summary>原生候选终态和提示词安全阻断，保留所有原生原因；不把安全过滤伪装为 API 错误。</summary>
internal sealed class VertexImageLifecycle
{
    private readonly HashSet<int> _seen = [];
    private readonly HashSet<int> _finished = [];
    private bool _blocked;
    /// <summary>逐帧记录原生候选关联与安全终态，不合并或改写载荷。</summary>
    internal bool Observe(JsonElement root)
    {
        if (_blocked && root.TryGetProperty("candidates", out var afterBlock) && afterBlock.ValueKind == JsonValueKind.Array && afterBlock.GetArrayLength() > 0)
            throw new InvalidDataException("Gemini 提示词阻断之后仍有候选内容。");
        var terminal = false;
        if (root.TryGetProperty("promptFeedback", out var feedback))
        {
            if (feedback.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini promptFeedback 必须为对象。");
            if (feedback.TryGetProperty("blockReason", out var reason) && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { Length: > 0 } block && block != "BLOCK_REASON_UNSPECIFIED") _blocked = terminal = true;
        }
        if (!root.TryGetProperty("candidates", out var candidates)) return terminal;
        if (candidates.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gemini candidates 必须为数组。");
        var frameIds = new HashSet<int>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 候选必须为对象。");
            var explicitIndex = candidate.TryGetProperty("index", out var index); var id = 0;
            if (explicitIndex && (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out id) || id < 0)) throw new InvalidDataException("Gemini 候选 index 无效。");
            if (!frameIds.Add(id)) throw new InvalidDataException("同帧 Gemini 候选 index 重复。");
            _seen.Add(id);
            if (candidate.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Gemini 候选 content 缺少原生 parts 数组。");
                if (_finished.Contains(id) && parts.GetArrayLength() > 0) throw new InvalidDataException("Gemini 候选终态之后仍有内容。");
            }
            if (candidate.TryGetProperty("finishReason", out var reason))
            {
                if (reason.ValueKind != JsonValueKind.String) throw new InvalidDataException("Gemini finishReason 必须为原生字符串。");
                if (reason.GetString() is { Length: > 0 } finish && finish != "FINISH_REASON_UNSPECIFIED")
                { if (!_finished.Add(id)) throw new InvalidDataException("Gemini 候选重复或改变终态。"); terminal = true; }
            }
        }
        return terminal;
    }
    /// <summary>全部已观察候选结束或原生提示词阻断后才允许 EOF。</summary>
    internal void Complete()
    { if (!_blocked && (_seen.Count == 0 || !_seen.SetEquals(_finished))) throw new InvalidDataException("Gemini 图片调用缺少各候选的原生终态或提示词阻断。"); }
}
