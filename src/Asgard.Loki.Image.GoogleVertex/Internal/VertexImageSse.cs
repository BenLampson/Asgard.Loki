using System.Runtime.CompilerServices;
using System.Text;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.GoogleVertex.Internal;

/// <summary>保留原始签名、图像与安全 SSE，生命周期只验证当前调用而不执行工具或续接。</summary>
internal static class VertexImageSse
{
    /// <summary>按原生帧透传，HTTP/API 错误保留事件身份并中断整次调用。</summary>
    internal static async IAsyncEnumerable<ImageGenerationEvent> ReadAsync(Stream source, ImageResultMetadata metadata, int status,
        string? contentType, [EnumeratorCancellation] CancellationToken token)
    {
        var lifecycle = new VertexImageLifecycle();
        await foreach (var frame in SseReader.ReadAsync(source, token, strictUtf8: true).ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(frame.Data); var root = VertexImageJson.Parse(bytes);
            var item = new ImageGenerationEvent(root, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (VertexImageJson.Error(root) || frame.EventName == "error")
                throw new ImageUpstreamException("Gemini 图片原生 SSE API 错误。", status, bytes, contentType) { GenerationEvent = item };
            lifecycle.Observe(root); yield return item;
        }
        lifecycle.Complete();
    }
    /// <summary>仅识别原生结束信号用于延迟发布，不创造新事件。</summary>
    internal static bool IsTerminal(ImageGenerationEvent value)
    {
        if (value.Data.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block)
            && block.ValueKind == System.Text.Json.JsonValueKind.String && block.GetString() is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED") return true;
        return value.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == System.Text.Json.JsonValueKind.String && finish.GetString() is { Length: > 0 } text && text != "FINISH_REASON_UNSPECIFIED");
    }
}
