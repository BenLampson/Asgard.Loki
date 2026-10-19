using System.Text.Json;

namespace Asgard.Loki.LLM.Ollama.Internal;

/// <summary>只校验原生记录身份和终态，不聚合正文、修补工具参数或推导用量。</summary>
internal sealed class OllamaChatState
{
    private string? _model;
    private bool _terminal;
    /// <summary>错误和 done 都属于原生终态，未来类型记录保留原文但不能代替终态。</summary>
    internal bool Push(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ollama 原生记录必须为 JSON 对象。");
        if (_terminal) throw new InvalidDataException("Ollama 原生终态之后仍有记录。");
        if (IsError(data)) return _terminal = true;
        var hasDone = data.TryGetProperty("done", out var done);
        if (!hasDone)
        {
            if (data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) return false;
            if (data.TryGetProperty("model", out _) || data.TryGetProperty("message", out _)) throw new InvalidDataException("Ollama Chat 记录缺少 done。");
            return false;
        }
        if (done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !data.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() is not { Length: > 0 } name
            || !data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ollama Chat 记录缺少有效 done、model 或 message。");
        if (_model is not null && _model != name) throw new InvalidDataException("Ollama 流中的模型身份发生变化。");
        _model = name;
        foreach (var property in new[] { "role", "content", "thinking" })
            if (message.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Ollama 消息已知文本字段类型无效。");
        if (data.TryGetProperty("done_reason", out var reason) && reason.ValueKind != JsonValueKind.String) throw new InvalidDataException("Ollama done_reason 必须是原生字符串。");
        return _terminal = done.GetBoolean();
    }
    /// <summary>没有明确原生终态时不制造 done 或 stop。</summary>
    internal void Complete() { if (!_terminal) throw new InvalidDataException("Ollama 流在原生终态之前断开。"); }
    /// <summary>错误原文保持独立，不能被 done 或其他貌似正常字段遮蔽。</summary>
    internal static bool IsError(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
