using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.XAI.Internal;

/// <summary>原生 xAI JSON 编辑引用和根字段快照，保留单图/多图来源及原始顺序。</summary>
internal static class XaiImageEditPayload
{
    /// <summary>认证及工厂调用前冻结输入，不把 image.url 改写为 OpenAI image_url 或 multipart。</summary>
    internal static byte[] Prepare(ImageEditRequest request, XaiImageEditOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters); XaiImageJson.Text(request.Model); XaiImageJson.Text(request.Prompt);
        var body = new JsonObject { ["model"] = options.Model, ["prompt"] = request.Prompt };
        foreach (var field in request.Parameters)
        {
            XaiImageJson.Text(field.Key); XaiImageJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("prompt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("扩展不能覆盖绑定模型或原始编辑 prompt。");
            if (field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Key != "stream" || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("原生 stream 必须为布尔值且不能使用大小写别名。");
                if (field.Value.GetBoolean()) throw new NotSupportedException("xAI 编辑入口仅执行完整原生 JSON，不推测 SSE 契约。");
            }
            body.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        var single = request.Parameters.TryGetValue("image", out var image); var multiple = request.Parameters.TryGetValue("images", out var images);
        if (single == multiple) throw new ArgumentException("原生编辑必须明确提供 image 或 images 之一，不能猜测来源优先级。");
        if (single) Reference(image);
        else
        {
            if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0) throw new ArgumentException("xAI 原生 images 必须为非空对象数组。");
            foreach (var item in images.EnumerateArray()) Reference(item);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("xAI 图片编辑请求超过本地字节上限。");
        return bytes;
    }
    /// <summary>引用只指向原生 URL/data URI 或既有 file_id，不读取文件、下载或改变字段名。</summary>
    private static void Reference(JsonElement image)
    {
        if (image.ValueKind != JsonValueKind.Object) throw new ArgumentException("xAI 原生图像引用必须为对象。");
        var url = image.TryGetProperty("url", out var address); var file = image.TryGetProperty("file_id", out var fileId);
        if (url == file) throw new ArgumentException("每个 xAI 引用必须有唯一原生 url 或 file_id。");
        var source = url ? address : fileId;
        if (source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString())) throw new ArgumentException("xAI 原生图像引用必须为非空文本。");
        if (image.TryGetProperty("type", out var type) && type.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("xAI 原生引用 type 必须为文本或 null。");
        if (!url) return;
        var value = address.GetString()!;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)) return;
        var separator = value.IndexOf(";base64,", StringComparison.Ordinal);
        if (value.StartsWith("data:image/", StringComparison.Ordinal) && separator > 11 && value.Length > separator + 8) return;
        throw new ArgumentException("xAI 原生图片 URL 必须为 HTTP(S) 或 Base64 image data URI。");
    }
}
