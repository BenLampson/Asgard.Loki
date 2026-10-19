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
        using var counted = new ImageCountingStream(source, expected); ImageEditEvent? terminal = null;
        await foreach (var frame in SseReader.ReadAsync(counted, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(frame.Data); var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生编辑事件必须为 JSON 对象。");
            if (frame.EventName == "error" || data.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)
                || data.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String && errorType.GetString() == "error")
                throw new ImageEditUpstreamException("OpenAI 原生编辑流返回了错误。", status, Encoding.UTF8.GetBytes(frame.Data), contentType)
                { EditEvent = new(data, false, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds) };
            ImageNativeJson.Unique(data);
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw new InvalidDataException("编辑事件缺少有效原生 type。");
            var name = type.GetString()!;
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name)
                throw new InvalidDataException("编辑 SSE event 与 JSON type 冲突。");
            if (terminal is not null) throw new InvalidDataException("编辑完成后仍有数据事件。");
            var done = ImageSseData.Validate(data, name, "image_edit");
            var item = new ImageEditEvent(data, done, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (done) terminal = item; else yield return item;
        }
        token.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("编辑 SSE 缺少原生 image_edit.completed。");
    }
}
