using System.Text.Json;

namespace Asgard.Loki.LLM.Perplexity.Internal;

/// <summary>原生 Responses 的稀疏终态、事件序号与响应身份，不合并或制造输出。</summary>
internal sealed class PerplexityStreamState
{
    private static readonly HashSet<string> Known = ["response.created", "response.in_progress", "response.completed", "response.failed",
        "response.output_item.added", "response.output_item.done", "response.output_text.delta", "response.output_text.done",
        "response.reasoning.started", "response.reasoning.stopped", "response.reasoning.search_queries", "response.reasoning.search_results",
        "response.reasoning.image_search_queries", "response.reasoning.image_search_results", "response.reasoning.fetch_url_queries", "response.reasoning.fetch_url_results"];
    private static readonly HashSet<string> Statuses = ["completed", "failed", "incomplete", "in_progress", "queued", "cancelled"];
    private string? _id;
    private long? _sequence;
    private bool _done;
    internal bool Finishing { get; private set; }
    /// <summary>完整非流式结果保留排队、失败和不完整状态，不把原生失败伪装成 HTTP 错误。</summary>
    internal static void Response(JsonElement data)
    {
        _ = String(data, "id"); _ = String(data, "model");
        if (String(data, "object") != "response" || !Statuses.Contains(String(data, "status"))
            || !data.TryGetProperty("created_at", out var created) || created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out _)
            || !data.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Perplexity 完整 response 缺少原生结构。");
        if (data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) Error(error);
    }
    /// <summary>只解释已知原生生命周期；未知事件及业务字段完整透传。</summary>
    internal void Push(JsonElement data, string? eventName)
    {
        if (_done) throw new InvalidDataException("Perplexity DONE 后出现数据。");
        var type = data.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (eventName is not (null or "" or "message") && Known.Contains(eventName) && eventName != type) throw new InvalidDataException("Perplexity SSE 与 JSON 原生事件类型不一致。");
        if (type is null || !Known.Contains(type)) return;
        if (eventName is not (null or "" or "message") && eventName != type) throw new InvalidDataException("Perplexity SSE 与 JSON 原生事件类型不一致。");
        if (Finishing) throw new InvalidDataException("Perplexity 终态后再次出现原生生成事件。");
        if (!data.TryGetProperty("sequence_number", out var sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var number)
            || _sequence is { } previous && number <= previous) throw new InvalidDataException("Perplexity 原生事件序号必须严格递增。");
        _sequence = number;
        if (data.TryGetProperty("response_id", out var responseId)) Identity(responseId);
        if (data.TryGetProperty("response", out var response))
        {
            if (response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Perplexity response 快照必须是对象。");
            if (response.TryGetProperty("id", out var id)) Identity(id);
            if (response.TryGetProperty("status", out var status))
            {
                if (status.ValueKind != JsonValueKind.String || !Statuses.Contains(status.GetString()!)) throw new InvalidDataException("Perplexity response 状态无效。");
                if (type is "response.completed" or "response.failed" && status.GetString() is "in_progress" or "queued") throw new InvalidDataException("Perplexity 终态带有未结束状态。");
            }
        }
        if (type == "response.failed")
        {
            if (!data.TryGetProperty("error", out var error)) throw new InvalidDataException("Perplexity failed 缺少原生 error。");
            Error(error); Finishing = true;
        }
        else if (type == "response.completed") Finishing = true;
    }
    /// <summary>控制标记只能位于原生终态之后，不能替代终态或重复出现。</summary>
    internal void Done()
    { if (!Finishing || _done) throw new InvalidDataException("Perplexity DONE 缺少唯一前置原生终态。"); _done = true; }
    /// <summary>EOF 必须已有原生 completed 或 failed；不推测或补发终态。</summary>
    internal void Complete()
    { if (!Finishing) throw new InvalidDataException("Perplexity SSE 缺少原生终态。"); }
    private void Identity(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException("Perplexity response ID 无效。");
        var id = value.GetString()!; if (_id is not null && _id != id) throw new InvalidDataException("Perplexity response ID 发生变化。"); _id = id;
    }
    private static void Error(JsonElement error)
    { if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) throw new InvalidDataException("Perplexity error 缺少原生 message。"); }
    private static string String(JsonElement value, string name)
    { if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new InvalidDataException("Perplexity response 缺少原生字符串字段。"); return item.GetString()!; }
}
