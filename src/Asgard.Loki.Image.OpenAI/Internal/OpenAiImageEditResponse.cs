using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>校验完整 JSON/HTTP 成功边界，保留原生结果/错误，不下载、解码或重建图片。</summary>
internal static class OpenAiImageEditResponse
{
    /// <summary>非流式成功入口只接受明确 JSON；失败状态仍保留完整任意原始正文。</summary>
    internal static void Headers(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) return;
        var type = response.Content.Headers.ContentType; var media = type?.MediaType;
        if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("编辑结果入口要求原生 JSON，不能将 SSE 或其他媒体替代为成功。");
        if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("编辑 JSON 只接受原生 UTF-8 编码。");
    }
    /// <summary>读尽并释放响应流后返回字节；声明长度/取消/读取或释放失败均不返回成功。</summary>
    internal static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var expected = response.Content.Headers.ContentLength;
        if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("编辑结果 Content-Length 无效。");
        using var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (expected is { } maximum && body.Length + read > maximum) throw new InvalidDataException("编辑结果超过声明长度。");
                await body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (expected is { } length && body.Length != length) throw new InvalidDataException("编辑结果未完整接收声明长度。");
        }
        token.ThrowIfCancellationRequested(); return body.ToArray();
    }
    /// <summary>检查原生容器和已知图像字段类型，完整保留顺序、用量与未来字段。</summary>
    internal static ImageGenerationResult Result(HttpResponseMessage response, byte[] body, ImageResultMetadata metadata)
    {
        if (!response.IsSuccessStatusCode) throw Failure(response, body);
        var text = new UTF8Encoding(false, true).GetString(body);
        using var document = JsonDocument.Parse(text.StartsWith('\uFEFF') ? text[1..] : text); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生编辑 JSON 必须为对象。");
        if (root.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)) throw Failure(response, body);
        Unique(root);
        if (!root.TryGetProperty("data", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new InvalidDataException("原生编辑响应缺少非空 data 图片数组。");
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object) throw new InvalidDataException("图片结果必须为对象。");
            var found = false;
            foreach (var name in new[] { "b64_json", "url", "revised_prompt" })
            {
                if (!image.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("已知原生图片字段必须为字符串。");
                if (name != "revised_prompt" && !string.IsNullOrWhiteSpace(value.GetString())) found = true;
            }
            if (!found) throw new InvalidDataException("图片结果缺少原生 b64_json/url。");
        }
        return new(root, metadata);
    }
    /// <summary>重复成员不按末值覆盖，避免原生输入/成功/错误含义不明确。</summary>
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("编辑 JSON 含重复成员。"); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }
    /// <summary>错误体不依赖 JSON/UTF-8 有效性，不伪造状态。</summary>
    private static ImageEditUpstreamException Failure(HttpResponseMessage response, byte[] body)
        => new("OpenAI 图像编辑返回了原生失败。", (int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
}
