using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>保留原始正文及原生内容类型，校验传输/JSON 成功边界，不重构转录结果。</summary>
internal static class OpenAiAudioFileResponse
{
    /// <summary>成功响应的意外 SSE/媒体类型在读取正文前拒绝；HTTP 失败仍保留完整原文。</summary>
    internal static void ValidateHeaders(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) return;
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || media.ToLowerInvariant() is "text/plain" or "text/vtt" or "text/srt" or "application/x-subrip" or "application/srt"))
            throw new InvalidDataException("文件结果入口未返回明确的非流式 JSON/文本结果类型。");
    }
    /// <summary>读尽并释放响应流后返回字节；错误、取消、声明长度不符或释放失败不返回成功。</summary>
    internal static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var expected = response.Content.Headers.ContentLength;
        if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("无效的文件结果 Content-Length。");
        using var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (expected is { } maximum && body.Length + read > maximum) throw new InvalidDataException("文件结果超过声明长度。");
                await body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (expected is { } length && body.Length != length) throw new InvalidDataException("文件结果未完整接收声明长度。");
        }
        token.ThrowIfCancellationRequested(); return body.ToArray();
    }

    /// <summary>原生业务错误完整保留；已知 JSON/text 输出不能被另一类型静默替代。</summary>
    internal static void Validate(HttpResponseMessage response, byte[] body, string? format)
    {
        var contentType = response.Content.Headers.ContentType; var media = contentType?.MediaType;
        if (!response.IsSuccessStatusCode) throw Failure(response, body);
        if (media is null || media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件结果入口未返回明确的非流式结果类型。");
        var json = media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        var text = media.ToLowerInvariant() is "text/plain" or "text/vtt" or "text/srt" or "application/x-subrip" or "application/srt";
        if (!json && !text) throw new InvalidDataException("文件结果具有不支持的内容类型。");
        if (json)
        {
            var value = new UTF8Encoding(false, true).GetString(body);
            using var document = JsonDocument.Parse(value.StartsWith('\uFEFF') ? value[1..] : value); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("文件 JSON 结果必须为原生对象。");
            if (root.EnumerateObject().Any(property => property.Name == "error" && property.Value.ValueKind != JsonValueKind.Null)) throw Failure(response, body);
            UniqueMembers(root);
            if (format is "text" or "srt" or "vtt") throw new InvalidDataException("原生文件格式与 JSON 响应不一致。");
            if (format is "json" or "verbose_json" or "diarized_json"
                && (!root.TryGetProperty("text", out var transcript) || transcript.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("原生文件 JSON 结果缺少字符串 text。");
        }
        else if (format is "json" or "verbose_json" or "diarized_json") throw new InvalidDataException("原生文件格式与文本响应不一致。");
    }

    /// <summary>不丢失原始状态、媒体类型或错误字节，包括非 JSON 和非 UTF-8 错误。</summary>
    private static AudioFileUpstreamException Failure(HttpResponseMessage response, byte[] body)
        => new("OpenAI 文件操作返回了原生失败。", (int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());

    /// <summary>重复成员使成功/错误解释存在歧义，应显式失败而不是选择末值。</summary>
    internal static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("原生文件 JSON 包含重复成员。"); UniqueMembers(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueMembers(item);
    }
}
