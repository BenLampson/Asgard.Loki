using System.Runtime.CompilerServices;
using System.Text;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Google.Internal;

/// <summary>保留原始签名、图像与安全 SSE，生命周期只验证当前调用而不执行工具或续接。</summary>
internal static class GoogleImageSse
{
    internal static async IAsyncEnumerable<ImageGenerationEvent> ReadAsync(Stream source, ImageResultMetadata metadata, int status,
        string? contentType, [EnumeratorCancellation] CancellationToken token)
    {
        var lifecycle = new GoogleImageLifecycle();
        await foreach (var frame in SseReader.ReadAsync(source, token, strictUtf8: true).ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(frame.Data); var root = GoogleImageJson.Parse(bytes);
            var item = new ImageGenerationEvent(root, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (GoogleImageJson.Error(root) || frame.EventName == "error")
                throw new ImageUpstreamException("Gemini 图片原生 SSE API 错误。", status, bytes, contentType) { GenerationEvent = item };
            lifecycle.Observe(root); yield return item;
        }
        lifecycle.Complete();
    }
    internal static bool IsTerminal(ImageGenerationEvent value)
    {
        if (value.Data.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block)
            && block.ValueKind == System.Text.Json.JsonValueKind.String && block.GetString() is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED") return true;
        return value.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(candidate => candidate.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == System.Text.Json.JsonValueKind.String && finish.GetString() is { Length: > 0 } text && text != "FINISH_REASON_UNSPECIFIED");
    }
}
