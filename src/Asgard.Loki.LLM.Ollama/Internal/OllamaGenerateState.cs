using System.Text.Json;

namespace Asgard.Loki.LLM.Ollama.Internal;

/// <summary>原生 generate 的独立响应/身份/终态状态，不复用 Chat 消息结构。</summary>
internal sealed class OllamaGenerateState
{
    private string? _model;
    private bool _terminal;
    /// <summary>未来对象允许原样透传，但不能制造完成；普通记录要求原生 response 文本。</summary>
    internal bool Push(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ollama generate 记录必须为 JSON 对象。");
        if (_terminal) throw new InvalidDataException("Ollama generate 终态之后仍有记录。");
        if (OllamaJson.IsError(data)) return _terminal = true;
        if (!data.TryGetProperty("done", out var done))
        {
            if (data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) return false;
            if (data.TryGetProperty("model", out _) || data.TryGetProperty("response", out _)) throw new InvalidDataException("Ollama generate 记录缺少 done。");
            return false;
        }
        if (done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !data.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() is not { Length: > 0 } name
            || !data.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Ollama generate 记录缺少有效 done、model 或 response。");
        if (_model is not null && _model != name) throw new InvalidDataException("Ollama generate 流中的模型身份发生变化。");
        _model = name;
        foreach (var property in new[] { "thinking", "done_reason" })
            if (data.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Ollama generate 原生文本字段类型无效。");
        return _terminal = done.GetBoolean();
    }
    /// <summary>传输关闭不能替代原生 done/error。</summary>
    internal void Complete() { if (!_terminal) throw new InvalidDataException("Ollama generate 流在原生终态前断开。"); }
}
