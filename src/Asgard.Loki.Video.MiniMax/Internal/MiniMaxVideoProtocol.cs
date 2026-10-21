using System.Globalization;
using System.Text.Json;

namespace Asgard.Loki.Video.MiniMax.Internal;

/// <summary>供应商自身的原生 v1/v2 结果、错误和状态，不跨版本猜测成功。</summary>
internal static class MiniMaxVideoProtocol
{
    /// <summary>v1 必须带 base_resp；v2 可以省略，但显式错误仍完整保留给调用异常。</summary>
    internal static bool HasApiError(JsonElement data, bool requireBase)
    {
        if (data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return true;
        if (!data.TryGetProperty("base_resp", out var response))
        {
            if (requireBase) throw new InvalidDataException("MiniMax v1 结果缺少原生 base_resp。"); return false;
        }
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("status_code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value))
            throw new InvalidDataException("MiniMax 原生 base_resp.status_code 无效。");
        return value != 0;
    }
    /// <summary>原生提交确认没有状态时只表示已接受，不合成排队、成功或结果。</summary>
    internal static VideoTaskSnapshot Submit(JsonElement data)
    {
        var id = MiniMaxVideoHttp.RequiredText(data, "task_id"); ValidateReturnedId(id);
        return new(id, null, VideoTaskState.Submitted, data);
    }
    /// <summary>明确选择原生结果结构，未知状态保持非终态，已知成功必须带对应版本结果。</summary>
    internal static VideoTaskSnapshot Query(JsonElement data, MiniMaxVideoApiVersion version, string expectedId)
    {
        var task = data;
        if (version == MiniMaxVideoApiVersion.V2 && (!data.TryGetProperty("task", out task) || task.ValueKind != JsonValueKind.Object)) throw new InvalidDataException("MiniMax v2 查询缺少原生 task 对象。");
        var id = MiniMaxVideoHttp.RequiredText(task, version == MiniMaxVideoApiVersion.V1 ? "task_id" : "id"); ValidateReturnedId(id);
        if (id != expectedId) throw new InvalidDataException("MiniMax 返回了其他任务的结果。");
        var status = MiniMaxVideoHttp.RequiredText(task, "status");
        var state = version == MiniMaxVideoApiVersion.V1 ? status switch
        {
            "Preparing" => VideoTaskState.Running, "Queueing" => VideoTaskState.Queued, "Processing" => VideoTaskState.Running,
            "Success" => VideoTaskState.Succeeded, "Fail" => VideoTaskState.Failed, _ => VideoTaskState.Unknown
        } : status switch
        {
            "queued" => VideoTaskState.Queued, "running" => VideoTaskState.Running, "succeeded" => VideoTaskState.Succeeded,
            "failed" => VideoTaskState.Failed, "cancelled" => VideoTaskState.Cancelled, _ => VideoTaskState.Unknown
        };
        if (state == VideoTaskState.Succeeded)
        {
            if (version == MiniMaxVideoApiVersion.V1) _ = FileId(task);
            else if (!task.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MiniMax v2 成功任务缺少原生 content 对象。");
            else _ = MiniMaxVideoHttp.RequiredText(content, "url");
        }
        return new(id, status, state, data);
    }
    /// <summary>结果文件标识是正 int64 十进制，不经过浮点数；原始 JSON 类型仍留在数据中。</summary>
    internal static string FileId(JsonElement data)
    {
        if (!data.TryGetProperty("file_id", out var id)) throw new InvalidDataException("成功视频结果缺少 file_id。");
        var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        if (string.IsNullOrEmpty(text) || text[0] == '0' || !text.All(char.IsAsciiDigit) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new InvalidDataException("视频结果文件标识必须是正 int64 十进制。");
        return text;
    }
    /// <summary>无效返回标识是协议错误，而非调用者参数错误。</summary>
    private static void ValidateReturnedId(string id)
    {
        try { MiniMaxVideoHttp.TaskId(id); } catch (ArgumentException error) { throw new InvalidDataException("MiniMax 返回无效任务标识。", error); }
    }
}
