using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Internal;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>生成专用的原生 SSE 终态与错误处理，不借用编辑状态或把 EOF/DONE 当完成。</summary>
internal static class OpenAiImageGenerationSse
{
    /// <summary>保留原始事件和独立 SSE 身份；终态等待 EOF，外层仍需成功释放传输资源。</summary>
    internal static async IAsyncEnumerable<ImageGenerationEvent> ReadAsync(Stream source, long? expected, ImageResultMetadata metadata,
        int status, string? contentType, [EnumeratorCancellation] CancellationToken token)
    {
        using var counted = new ImageCountingStream(source, expected); ImageGenerationEvent? terminal = null;
        await foreach (var frame in SseReader.ReadAsync(counted, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(frame.Data); var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生生成事件必须为 JSON 对象。");
            if (frame.EventName is "error" or "image_generation.failed"
                || data.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)
                || data.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String && errorType.GetString() is "error" or "image_generation.failed")
                throw new ImageUpstreamException("OpenAI 原生图像生成流失败。", status, Encoding.UTF8.GetBytes(frame.Data), contentType)
                { GenerationEvent = new(data, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds) };
            ImageNativeJson.Unique(data);
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw new InvalidDataException("生成事件缺少有效原生 type。");
            var name = type.GetString()!;
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name) throw new InvalidDataException("生成 SSE event 与 JSON type 冲突。");
            if (terminal is not null) throw new InvalidDataException("生成完成后仍出现数据事件。");
            var done = ImageSseData.Validate(data, name, "image_generation");
            var item = new ImageGenerationEvent(data, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (done) terminal = item; else yield return item;
        }
        token.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("生成 SSE 缺少原生 image_generation.completed。");
    }
}
