using System.Runtime.CompilerServices;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>单次 Azure Image 原生事件验证，不重组图片、配对不存在的输出 ID 或执行用量计算。</summary>
internal static class AzureImageSse
{
    /// <summary>按原始顺序交付事件；外层暂存首个完成及其后所有数据直到 EOF/资源释放全部成功。</summary>
    internal static async IAsyncEnumerable<AzureImageStreamFrame> ReadAsync(Stream source, string family, int expectedImages,
        Func<AzureImageStreamFrame, Exception> failure, [EnumeratorCancellation] CancellationToken token)
    {
        var completed = 0; var done = false;
        await foreach (var frame in SseReader.ReadAsync(source, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (frame.Data.Trim() == "[DONE]")
            {
                if (done || completed != expectedImages) throw new InvalidDataException("Azure 图像 DONE 不能替代缺失原生完成项或重复终止。");
                done = true; continue;
            }
            JsonElement data;
            try
            {
                using var document = JsonDocument.Parse(frame.Data, new JsonDocumentOptions { AllowDuplicateProperties = false });
                data = document.RootElement; AzureImageJson.Validate(data);
                if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 图像 SSE 数据必须为原生对象。");
                data = data.Clone();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
            { throw new InvalidDataException("Azure 图像 SSE 原生 JSON 或编码无效。", error); }
            var hasType = data.TryGetProperty("type", out var value); var name = hasType && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (frame.EventName is "error" or "upstream_error" or "image_generation.failed" or "image_edit.failed"
                || name is "error" or "upstream_error" or "image_generation.failed" or "image_edit.failed"
                || data.TryGetProperty("error", out var nativeError) && nativeError.ValueKind != JsonValueKind.Null)
                throw failure(new(data, frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, completed > 0, false));
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Azure 图像 SSE 缺少原生 type 字符串。");
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name)
                throw new InvalidDataException("Azure 图像 SSE event 与原生 type 不一致。");
            if (name.StartsWith(family == "image_generation" ? "image_edit." : "image_generation.", StringComparison.Ordinal))
                throw new InvalidDataException("Azure 图像 SSE 返回了其他原生操作的事件。");
            if (done || completed == expectedImages) throw new InvalidDataException("Azure 图像调用完成后仍有原生数据事件。");
            var partial = name == family + ".partial_image"; var final = name == family + ".completed";
            if (partial || final) Image(data, partial);
            if (final) completed++;
            yield return new(data, frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, completed > 0, final && completed == expectedImages);
        }
        token.ThrowIfCancellationRequested();
        if (completed != expectedImages) throw new InvalidDataException("Azure 图像 SSE 缺少请求数量的原生完成图片。");
    }
    /// <summary>仅验证已知结构，不把 partial 数量当承诺，不按索引跨图片臆造关联。</summary>
    private static void Image(JsonElement data, bool partial)
    {
        if (!data.TryGetProperty("b64_json", out var image) || image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
            throw new InvalidDataException("Azure 图像原生事件缺少 b64_json。");
        if (partial && (!data.TryGetProperty("partial_image_index", out var index) || !AzureImageNumber.Integer(index)))
            throw new InvalidDataException("Azure 图像原生部分图片索引必须是非负整数。");
        foreach (var name in new[] { "background", "output_format", "quality", "size" })
            if (data.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Azure 图像原生图片属性必须是字符串。");
        if (data.TryGetProperty("created_at", out var created) && created.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("Azure 图像原生时间必须保持数字。");
        if (!data.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return;
        if (usage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 图像原生用量必须是对象。");
        foreach (var name in new[] { "input_tokens", "output_tokens", "total_tokens" })
            if (usage.TryGetProperty(name, out var value) && !AzureImageNumber.Integer(value)) throw new InvalidDataException("Azure 图像原生用量计数必须是非负整数。");
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null)
        {
            if (details.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 图像原生用量明细必须是对象。");
            foreach (var name in new[] { "text_tokens", "image_tokens" })
                if (details.TryGetProperty(name, out var value) && !AzureImageNumber.Integer(value)) throw new InvalidDataException("Azure 图像原生用量明细计数必须是非负整数。");
        }
    }
}
