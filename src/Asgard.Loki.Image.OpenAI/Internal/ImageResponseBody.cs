namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>Image 供应商内部的纯 HTTP 正文字节校验，不涉及能力路由、媒体处理或调用编排。</summary>
internal static class ImageResponseBody
{
    /// <summary>非流式成功必须是 UTF-8 JSON；HTTP 失败保留任意类型原始正文。</summary>
    internal static void RequireJson(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) return;
        var type = response.Content.Headers.ContentType; var media = type?.MediaType;
        if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("图像结果入口要求原生 JSON，不接受 SSE/其他媒体替代。");
        if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("图像 JSON 必须为原生 UTF-8。");
    }
    /// <summary>正常 EOF、声明长度和源释放全部成功后返回字节，不探测源流 Length/Position。</summary>
    internal static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var expected = response.Content.Headers.ContentLength;
        if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("图像结果 Content-Length 无效。");
        using var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (expected is { } maximum && body.Length + read > maximum) throw new InvalidDataException("图像正文超过声明长度。");
                await body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (expected is { } length && body.Length != length) throw new InvalidDataException("图像正文未完整接收声明长度。");
        }
        token.ThrowIfCancellationRequested(); return body.ToArray();
    }
}
