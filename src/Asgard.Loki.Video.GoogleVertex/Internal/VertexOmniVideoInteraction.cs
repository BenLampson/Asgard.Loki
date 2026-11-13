using System.Text.Json;

namespace Asgard.Loki.Video.GoogleVertex.Internal;

/// <summary>识别 Interactions 视频的原生身份、任务状态和已完成视频结果。</summary>
internal static class VertexOmniVideoInteraction
{
    /// <summary>保留完整 JSON；原生完成但没有输出视频不能成为视频任务成功。</summary>
    internal static VideoTaskSnapshot Parse(JsonElement data, string model, string? expectedId)
    {
        var id = Text(data, "id") ?? throw new InvalidDataException("Vertex Interactions 缺少原生任务 ID。");
        try { VertexVideoJson.Segment(id); }
        catch (ArgumentException error) { throw new InvalidDataException("Vertex Interactions 返回无效任务 ID。", error); }
        if (expectedId is not null && id != expectedId)
            throw new InvalidDataException("Vertex Interactions 查询返回不同任务 ID。");
        if (data.TryGetProperty("object", out _) && Text(data, "object") != "interaction"
            || data.TryGetProperty("model", out _) && Text(data, "model") != model)
            throw new InvalidDataException("Vertex Interactions 返回不同对象类型或实际模型。");
        var status = Text(data, "status") ?? throw new InvalidDataException("Vertex Interactions 缺少原生任务状态。");
        var state = status switch
        {
            "queued" => VideoTaskState.Queued,
            "in_progress" => VideoTaskState.Running,
            "completed" => HasVideo(data) ? VideoTaskState.Succeeded
                : throw new InvalidDataException("Vertex Interactions 已完成，但缺少原生视频输出。"),
            "failed" or "incomplete" => VideoTaskState.Failed,
            "cancelled" => VideoTaskState.Cancelled,
            _ => VideoTaskState.Unknown
        };
        return new(id, status, state, data);
    }

    /// <summary>只确认原生视频输出的存在；不下载 URI 或解码内联媒体。</summary>
    private static bool HasVideo(JsonElement data)
    {
        if (!data.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return false;
        foreach (var step in steps.EnumerateArray())
        {
            if (Text(step, "type") != "model_output" || !step.TryGetProperty("content", out var items)
                || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
                if (Text(item, "type") == "video" && (Text(item, "data") is not null || Text(item, "uri") is not null))
                    return true;
        }
        return false;
    }

    /// <summary>读取非空的原生字符串，不对未知 JSON 结构做类型强制转换。</summary>
    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String && field.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
