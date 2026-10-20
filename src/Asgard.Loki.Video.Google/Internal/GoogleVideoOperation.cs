using System.Text.Json;

namespace Asgard.Loki.Video.Google.Internal;

/// <summary>解析原生长操作；保留过滤结果与错误，不从 HTTP 200 推断视频已生成。</summary>
internal static class GoogleVideoOperation
{
    /// <summary>原生名称必须属于配置的模型，直接保留，不生成中转任务标识。</summary>
    internal static string Name(string name, string model)
    {
        var parts = name.Split('/');
        if (parts.Length != 4 || parts[0] != "models" || parts[1] != model || parts[2] != "operations") throw new ArgumentException("操作名称不属于当前完整视频目标。");
        GoogleVideoHttp.Segment(parts[3]); return name;
    }
    /// <summary>done 缺省是运行中；有结果或错误却没有 done=true 时拒绝矛盾形状。</summary>
    internal static VideoTaskSnapshot Parse(JsonElement data, string model, string? expectedName)
    {
        if (!data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) throw new InvalidDataException("Gemini 操作缺少原生名称。");
        var id = name.GetString()!;
        try { Name(id, model); } catch (ArgumentException error) { throw new InvalidDataException("Gemini 返回了其他目标或无效操作名称。", error); }
        if (expectedName is not null && id != expectedName) throw new InvalidDataException("Gemini 查询返回了其他操作。");
        var hasDone = data.TryGetProperty("done", out var done);
        if (hasDone && done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("原生 done 必须是布尔值。");
        var hasError = data.TryGetProperty("error", out var errorValue); var hasResponse = data.TryGetProperty("response", out var response);
        if (!hasDone || !done.GetBoolean())
        {
            if (hasError || hasResponse) throw new InvalidDataException("未完成操作不能携带原生终态结果。");
            return new(id, hasDone ? "false" : null, VideoTaskState.Running, data);
        }
        if (hasError)
        {
            if (hasResponse || errorValue.ValueKind != JsonValueKind.Object || !errorValue.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number == 0)
                throw new InvalidDataException("Gemini 操作错误结构无效或与结果冲突。");
            return new(id, "true", VideoTaskState.Failed, data);
        }
        if (!hasResponse || response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("generateVideoResponse", out var generated) || generated.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("已完成操作缺少原生视频响应。");
        var count = 0;
        if (generated.TryGetProperty("generatedSamples", out var samples))
        {
            if (samples.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生视频样本必须为数组。");
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object
                    || !video.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(uri.GetString()))
                    throw new InvalidDataException("原生视频样本缺少结果 URI。");
                count++;
            }
        }
        if (count > 0) return new(id, "true", VideoTaskState.Succeeded, data);
        if (generated.TryGetProperty("raiMediaFilteredCount", out var filtered) && filtered.ValueKind == JsonValueKind.Number && filtered.TryGetInt32(out var filteredCount) && filteredCount > 0)
            return new(id, "true", VideoTaskState.Failed, data);
        throw new InvalidDataException("已完成操作既没有视频样本，也没有明确过滤结果。");
    }
}
