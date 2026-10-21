using System.Text.Json;

namespace Asgard.Loki.LLM.GoogleVertex.Internal;

/// <summary>仅检查原生候选/提示拦截生命周期，不聚合内容、工具或用量。</summary>
internal sealed class VertexStreamState(int maximumCandidates)
{
    private readonly HashSet<int> _seen = [];
    private readonly HashSet<int> _finished = [];
    private bool _blocked;
    private string? _id;
    private string? _modelVersion;
    /// <summary>首次可能终态后保留整个原序尾部，直到 EOF 和资源释放。</summary>
    internal bool Finishing => _blocked || _finished.Count != 0;
    /// <summary>原生 index 缺省时按 protobuf 的零值关联，但不向事件补写字段。</summary>
    internal void Observe(JsonElement data)
    {
        Identity(data, "responseId", ref _id); Identity(data, "modelVersion", ref _modelVersion);
        if (data.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind != JsonValueKind.Null)
        {
            if (feedback.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex promptFeedback 必须是对象。");
            if (feedback.TryGetProperty("blockReason", out var block) && block.ValueKind != JsonValueKind.Null)
            {
                if (SpecifiedEnum(block, "BLOCK_REASON_UNSPECIFIED"))
                {
                    if (_blocked || _seen.Count != 0) throw new InvalidDataException("Vertex 提示拦截重复或出现在生成候选之后。"); _blocked = true;
                }
            }
        }
        if (!data.TryGetProperty("candidates", out var candidates)) return;
        if (candidates.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Vertex candidates 必须是数组。");
        var indices = new HashSet<int>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || _blocked) throw new InvalidDataException("Vertex 候选无效或在提示拦截后出现。");
            var index = 0;
            if (candidate.TryGetProperty("index", out var nativeIndex)) index = VertexInteger.Read(nativeIndex);
            if (!indices.Add(index)) throw new InvalidDataException("Vertex 同一帧内重复候选索引。");
            if (_seen.Add(index) && _seen.Count > maximumCandidates) throw new InvalidDataException("Vertex 候选关联数超过配置上限。");
            if (candidate.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
            {
                if (content.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex 候选 content 必须是对象。");
                if (content.TryGetProperty("parts", out var parts))
                {
                    if (parts.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Vertex 候选 parts 必须是数组。");
                    if (_finished.Contains(index) && parts.GetArrayLength() != 0) throw new InvalidDataException("Vertex 已结束候选之后仍有内容。");
                }
            }
            if (!candidate.TryGetProperty("finishReason", out var finish) || finish.ValueKind == JsonValueKind.Null) continue;
            if (SpecifiedEnum(finish, "FINISH_REASON_UNSPECIFIED") && !_finished.Add(index)) throw new InvalidDataException("Vertex 候选重复结束或改变结束原因。");
        }
    }
    /// <summary>未知对象或用量不能替代缺失的候选原生终态；提示拦截保留独立原生语义。</summary>
    internal void Complete()
    {
        if (!_blocked && (_seen.Count == 0 || !_seen.SetEquals(_finished))) throw new InvalidDataException("Vertex SSE 在原生候选终态之前截断。");
    }
    /// <summary>ProtoJSON 原生枚举可为名称或 int32 数字；零/unspecified 不补造终态。</summary>
    private static bool SpecifiedEnum(JsonElement value, string unspecified) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: > 0 } text && text != unspecified,
        JsonValueKind.Number => VertexInteger.Read(value, signed: true) != 0,
        _ => throw new InvalidDataException("Vertex 原生枚举必须是名称或 int32 数字。")
    };
    /// <summary>原始响应/版本身份一致；不要求服务返回部署别名，也不生成缺失身份。</summary>
    private static void Identity(JsonElement data, string name, ref string? captured)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Vertex 原生流身份必须是字符串。");
        var text = value.GetString()!; if (text.Length == 0) return;
        if (captured is not null && captured != text) throw new InvalidDataException("Vertex 原生响应身份在流中变化。"); captured = text;
    }
}
