using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Internal;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>仅处理单次原生编辑 SSE，保持事件内容与身份，不重组图片、跨输出配对或推导缺失终态。</summary>
internal static class OpenAiImageEditSse
{
    /// <summary>原生完成项延迟到正常 EOF，调用方还要等待响应流/消息/客户端成功释放。</summary>
    internal static async IAsyncEnumerable<ImageEditEvent> ReadAsync(Stream source, long? expected, ImageResultMetadata metadata,
        int status, string? contentType, [EnumeratorCancellation] CancellationToken token)
    {
        using var counted = new ImageEditCountingStream(source, expected); ImageEditEvent? terminal = null;
        await foreach (var frame in SseReader.ReadAsync(counted, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(frame.Data); var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生编辑事件必须为 JSON 对象。");
            if (frame.EventName == "error" || data.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)
                || data.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String && errorType.GetString() == "error")
                throw new ImageEditUpstreamException("OpenAI 原生编辑流返回了错误。", status, Encoding.UTF8.GetBytes(frame.Data), contentType)
                { EditEvent = new(data, false, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds) };
            OpenAiImageEditResponse.Unique(data);
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw new InvalidDataException("编辑事件缺少有效原生 type。");
            var name = type.GetString()!;
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name)
                throw new InvalidDataException("编辑 SSE event 与 JSON type 冲突。");
            if (terminal is not null) throw new InvalidDataException("编辑完成后仍有数据事件。");
            var done = Validate(data, name);
            var item = new ImageEditEvent(data, done, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (done) terminal = item; else yield return item;
        }
        token.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("编辑 SSE 缺少原生 image_edit.completed。");
    }
    /// <summary>只校验已知事件结构，不臆造输出关联、partial 数量承诺或用量加法。</summary>
    private static bool Validate(JsonElement data, string name)
    {
        if (name is not ("image_edit.partial_image" or "image_edit.completed")) return false;
        if (!data.TryGetProperty("b64_json", out var image) || image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
            throw new InvalidDataException("原生编辑图片事件缺少 b64_json。");
        if (name == "image_edit.partial_image"
            && (!data.TryGetProperty("partial_image_index", out var index) || !ImageEditJsonNumber.IsNonNegativeInteger(index)))
            throw new InvalidDataException("原生部分图像索引必须为非负整数。");
        foreach (var field in new[] { "background", "output_format", "quality", "size" })
            if (data.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("已知原生编辑图片属性必须为字符串。");
        if (data.TryGetProperty("created_at", out var created) && !ImageEditJsonNumber.IsNonNegativeInteger(created))
            throw new InvalidDataException("编辑事件 created_at 必须为非负整数。");
        if (data.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生图片用量必须为对象。");
            foreach (var field in new[] { "input_tokens", "output_tokens", "total_tokens" })
                if (usage.TryGetProperty(field, out var count) && !ImageEditJsonNumber.IsNonNegativeInteger(count))
                    throw new InvalidDataException("原生图片用量计数必须为非负整数。");
            if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null)
            {
                if (details.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生图片输入用量明细必须为对象。");
                foreach (var field in new[] { "text_tokens", "image_tokens" })
                    if (details.TryGetProperty(field, out var count) && !ImageEditJsonNumber.IsNonNegativeInteger(count))
                        throw new InvalidDataException("原生图片输入用量计数必须为非负整数。");
            }
        }
        return name == "image_edit.completed";
    }
}
