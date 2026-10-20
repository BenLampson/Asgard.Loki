using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.SunoAPI.Internal;

/// <summary>供应商本地 JSON、任务关联和终态验证，不依赖其他能力。</summary>
internal static class SunoAudioJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损文本编码。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>操作为显式原生名，不接受模型名猜测或任意 URL 操作。</summary>
    internal static void Operation(string value)
    {
        if (value is not ("MUSIC" or "LYRICS")) throw new ArgumentException("Suno-API 只支持显式 MUSIC 或 LYRICS 操作。");
    }
    /// <summary>标识可包含需转义字符，但不能形成跳转段或控制字符。</summary>
    internal static void TaskId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value);
        if (value is "." or ".." || value.Any(char.IsControl)) throw new ArgumentException("Suno-API 任务标识无效。");
    }
    /// <summary>完整验证未知字段、精确数字、UTF-8 和重复字段。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header)
            || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is not null && !header.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Suno-API 接口必须返回 UTF-8 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Suno-API 响应必须是完整原生对象。");
            var pending = new Stack<JsonElement>(); pending.Push(document.RootElement);
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var member in value.EnumerateObject()) { Text(member.Name); pending.Push(member.Value); }
            }
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException or EncoderFallbackException)
        { throw new InvalidDataException("Suno-API 原生 JSON 或编码无效。", error); }
    }
    /// <summary>状态、身份和错误码必须保持原生非空字符串。</summary>
    internal static string Required(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
        ? item.GetString()! : throw new InvalidDataException("Suno-API 响应缺少有效 " + name + "。");
    /// <summary>保留实际任务记录，验证 ID/操作和明确原生终态。</summary>
    internal static AudioTaskSnapshot Window(JsonElement record, AudioTaskQuery query)
    {
        if (record.ValueKind != JsonValueKind.Object || Required(record, "task_id") != query.TaskId || Required(record, "action") != query.Operation)
            throw new InvalidDataException("Suno-API 返回其他任务或操作的窗口。");
        var native = Required(record, "status");
        var state = native switch
        {
            "NOT_START" or "SUBMITTED" => AudioTaskState.Submitted, "QUEUED" => AudioTaskState.Queued,
            "IN_PROGRESS" => AudioTaskState.Running, "SUCCESS" => AudioTaskState.Succeeded,
            "FAILURE" => AudioTaskState.Failed, _ => AudioTaskState.Unknown
        };
        var hasData = record.TryGetProperty("data", out var data);
        if (hasData) data = ResultData(data, query.TaskId);
        if (state == AudioTaskState.Succeeded)
        {
            if (record.TryGetProperty("fail_reason", out var reason) && (reason.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(reason.GetString())))
                throw new InvalidDataException("Suno-API 成功窗口包含矛盾的错误。");
            if (!hasData) throw new InvalidDataException("Suno-API 成功窗口缺少结果。");
            var items = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : new[] { data };
            if (items.Length == 0) throw new InvalidDataException("Suno-API 成功窗口没有原生结果。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Suno-API 原生结果必须是对象。");
                if (item.TryGetProperty("status", out var resultStatus) && (resultStatus.ValueKind != JsonValueKind.String
                    || resultStatus.GetString() is "error" or "failed" or "streaming" or "submitted" or "queued"))
                    throw new InvalidDataException("Suno-API 成功任务包含失败或未完成的原生结果。");
                if (query.Operation == "LYRICS") _ = Required(item, "text");
                else
                {
                    if (!ids.Add(Required(item, "id"))) throw new InvalidDataException("Suno-API 成功歌曲 ID 重复。");
                    var audio = Address(item, "audio_url"); var video = Address(item, "video_url");
                    if (!audio && !video) throw new InvalidDataException("Suno-API 成功歌曲没有原生媒体地址。");
                    if (item.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                        foreach (var name in new[] { "error_type", "error_message" })
                            if (metadata.TryGetProperty(name, out var error) && error.ValueKind != JsonValueKind.Null
                                && (error.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(error.GetString())))
                                throw new InvalidDataException("Suno-API 成功歌曲包含原生错误。");
                }
            }
        }
        return new(query.TaskId, query.Operation, native, state, record);
    }
    /// <summary>只识别已知旧包装，关联在非终态也必须有效；不改写公开 JSON。</summary>
    internal static JsonElement ResultData(JsonElement data, string taskId)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty("task_id", out _))
        {
            if (Required(nested, "task_id") != taskId || !nested.TryGetProperty("data", out data))
                throw new InvalidDataException("Suno-API 嵌套结果关联了其他任务。");
        }
        return data;
    }
    /// <summary>验证原始地址，不下载、不重新编码也不替换签名。</summary>
    private static bool Address(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Suno-API 媒体地址必须是字符串。");
        var text = value.GetString()!; if (text.Length == 0) return false;
        if (text.Any(char.IsWhiteSpace) || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0) throw new InvalidDataException("Suno-API 媒体地址无效。");
        return true;
    }
}
