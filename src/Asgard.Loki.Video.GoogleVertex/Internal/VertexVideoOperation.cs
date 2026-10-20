using System.Text.Json;

namespace Asgard.Loki.Video.GoogleVertex.Internal;

/// <summary>解释 Vertex Veo 原生长操作，完整保留 videos、GCS 地址、内联字节与过滤信息。</summary>
internal static class VertexVideoOperation
{
    /// <summary>不将提交确认或未知结果猜测为成功，也不将原生结果转换成另一协议。</summary>
    internal static VideoTaskSnapshot Parse(JsonElement data, string resource, string? expectedName)
    {
        if (!data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) throw new InvalidDataException("Vertex 操作缺少原生名称。");
        var id = name.GetString()!;
        try { VertexVideoJson.OperationName(id, resource); } catch (ArgumentException error) { throw new InvalidDataException("Vertex 返回了无效或其他目标的操作名称。", error); }
        if (expectedName is not null && id != expectedName) throw new InvalidDataException("Vertex 返回了其他操作的结果。");
        var hasDone = data.TryGetProperty("done", out var done);
        if (hasDone && done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Vertex 原生 done 必须是布尔值。");
        var hasError = data.TryGetProperty("error", out var errorValue); var hasResponse = data.TryGetProperty("response", out var response);
        if (!hasDone || !done.GetBoolean())
        {
            if (hasError || hasResponse) throw new InvalidDataException("未完成 Vertex 操作不能携带终态结果。");
            return new(id, hasDone ? "false" : null, VideoTaskState.Running, data);
        }
        if (hasError)
        {
            if (hasResponse || errorValue.ValueKind != JsonValueKind.Object || !errorValue.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number == 0)
                throw new InvalidDataException("Vertex 原生错误结构无效或与结果冲突。");
            return new(id, "true", VideoTaskState.Failed, data);
        }
        if (!hasResponse || response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("已完成 Vertex 操作缺少原生响应。");
        var count = 0;
        if (response.TryGetProperty("videos", out var videos))
        {
            if (videos.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生 videos 必须是数组。");
            foreach (var video in videos.EnumerateArray())
            {
                if (video.ValueKind != JsonValueKind.Object || !(NonEmptyText(video, "gcsUri") || NonEmptyText(video, "bytesBase64Encoded")))
                    throw new InvalidDataException("Vertex 视频结果缺少原生 GCS URI 或内联媒体。");
                count++;
            }
        }
        if (count > 0) return new(id, "true", VideoTaskState.Succeeded, data);
        if (response.TryGetProperty("raiMediaFilteredCount", out var filtered) && filtered.ValueKind == JsonValueKind.Number && filtered.TryGetInt32(out var numberFiltered) && numberFiltered > 0)
            return new(id, "true", VideoTaskState.Failed, data);
        throw new InvalidDataException("已完成 Vertex 操作没有视频结果或明确过滤结果。");
    }
    /// <summary>仅检查原生结果载体存在，不解码、下载、重写或证明视频内容有效。</summary>
    private static bool NonEmptyText(JsonElement value, string field)
        => value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString());
}
