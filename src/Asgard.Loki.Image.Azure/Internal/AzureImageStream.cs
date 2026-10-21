using System.Runtime.CompilerServices;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>Image 供应商本地单次 SSE 传输，不与 LLM、音频或任务协调器共享状态。</summary>
internal static class AzureImageStream
{
    /// <summary>普通进度立即交付，首个原生完成起的尾部保持原序等待所有 HTTP 资源正常释放。</summary>
    internal static async IAsyncEnumerable<AzureImageStreamFrame> ReadAsync(AzureImageTarget target, string operation, Func<HttpContent> content,
        int expectedImages, ImageResultMetadata metadata, Func<AzureImageResponse, AzureImageStreamFrame?, Exception> failure,
        [EnumeratorCancellation] CancellationToken token)
    {
        var tail = new List<AzureImageStreamFrame>(); token.ThrowIfCancellationRequested();
        using (var message = target.Message(operation, content(), true))
        using (var client = target.Client())
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            target.Validate(response, operation); var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, target.Options.MaxResponseBytes, token).ConfigureAwait(false);
                var original = new AzureImageResponse(response.StatusCode, bytes, type?.ToString());
                _ = AzureImageJson.Result(original, metadata, () => failure(original, null));
                throw new InvalidDataException("Azure 原生图像流未返回 SSE，不从 JSON 构造假事件。", failure(original, null));
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Azure 图像 SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null || expected < 0 || expected > target.Options.MaxResponseBytes)
                throw new InvalidDataException("Azure 图像 SSE 声明长度无效或超过显式上限。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                using var counted = new AzureImageReadStream(source, expected, target.Options.MaxResponseBytes);
                await foreach (var frame in AzureImageSse.ReadAsync(counted, operation == "generations" ? "image_generation" : "image_edit", expectedImages,
                    item => failure(new(response.StatusCode, System.Text.Encoding.UTF8.GetBytes(item.RawData), type?.ToString()), item), token).ConfigureAwait(false))
                {
                    if (frame.AfterCompletion) tail.Add(frame); else yield return frame;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        foreach (var frame in tail) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
}
