using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>原生编辑输入快照和 JSON/multipart 编码，不跨协议推导引用或隐式转换媒体。</summary>
internal static class OpenAiImageEditPayload
{
    private static readonly Encoding NativeUtf8 = new UTF8Encoding(false, true);
    /// <summary>直接适配器入口也验证/复制 JSON 引用与原生字段，不依赖核心路由曾经校验。</summary>
    internal static ImageEditRequest Snapshot(ImageEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.ContainsKey("model") || request.Parameters.ContainsKey("prompt")) throw new ArgumentException("编辑参数不能覆盖绑定字段。");
        foreach (var pair in request.Parameters)
        { Text(pair.Key); if (pair.Value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("编辑 JSON 不能含 Undefined。"); OpenAiImageEditResponse.Unique(pair.Value); }
        var parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!parameters.TryGetValue("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new ArgumentException("JSON 编辑要求非空 images 原生引用数组。");
        foreach (var image in images.EnumerateArray()) Reference(image);
        if (parameters.TryGetValue("mask", out var mask) && mask.ValueKind != JsonValueKind.Null) Reference(mask);
        if (parameters.TryGetValue("stream", out var stream))
        {
            if (stream.ValueKind == JsonValueKind.True) throw new NotSupportedException("stream=true 必须使用独立编辑流式入口。");
            if (stream.ValueKind != JsonValueKind.False) throw new ArgumentException("原生 stream 必须为布尔值。");
        }
        return request with { Parameters = parameters };
    }
    /// <summary>复制表单元数据，保持多图和 mask 的独立流身份/顺序；不检查像素、尺寸或编码。</summary>
    internal static ImageMultipartEditRequest Snapshot(ImageMultipartEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Images); ArgumentNullException.ThrowIfNull(request.Fields);
        var sources = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        var images = request.Images.Select(value => Upload(value, sources)).ToArray(); ArgumentOutOfRangeException.ThrowIfZero(images.Length);
        var mask = request.Mask is null ? null : Upload(request.Mask, sources); var fields = request.Fields.ToArray();
        var scalars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); Text(field.Value);
            if (field.Name is "model" or "prompt" or "image" or "image[]" or "images" or "mask") throw new ArgumentException("表单扩展不能覆盖绑定的编辑输入。");
            if (field.Name is "stream" or "response_format" or "background" or "input_fidelity" or "n" or "output_compression"
                or "output_format" or "quality" or "size" or "user" or "partial_images" or "moderation")
            { if (!scalars.Add(field.Name)) throw new ArgumentException("已知原生单值字段不能重复。"); }
            if (field.Name == "stream")
            {
                if (field.Value == "true") throw new NotSupportedException("stream=true 必须使用独立编辑流式入口。");
                if (field.Value != "false") throw new ArgumentException("表单 stream 必须为原生 true/false 文本。");
            }
        }
        return request with { Images = Array.AsReadOnly(images), Mask = mask, Fields = Array.AsReadOnly(fields) };
    }
    /// <summary>绑定实际模型，其余 JSON 值保留原生类型/数值，不注入默认 stream 或格式。</summary>
    internal static HttpContent Json(ImageEditRequest request, string model)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("model", model); writer.WriteString("prompt", request.Prompt);
            foreach (var pair in request.Parameters) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(bytes.ToArray()); content.Headers.ContentType = new("application/json"); return content;
    }
    /// <summary>原生 image[] parts 按输入顺序发送，mask 单独命名；文本字段不展开/改名。</summary>
    internal static HttpContent Multipart(ImageMultipartEditRequest request, string model)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(new StringContent(model, NativeUtf8), "model"); content.Add(new StringContent(request.Prompt, NativeUtf8), "prompt");
            foreach (var image in request.Images) Add(content, image, "image[]");
            if (request.Mask is { } mask) Add(content, mask, "mask");
            foreach (var field in request.Fields) content.Add(new StringContent(field.Value, NativeUtf8), field.Name);
            return content;
        }
        catch { content.Dispose(); throw; }
    }
    /// <summary>只编码原生头部和借用流，不按扩展名猜测 Content-Type。</summary>
    private static void Add(MultipartFormDataContent content, ImageUpload upload, string name)
    {
        var part = new BorrowedImageContent(upload.Content);
        try { if (upload.ContentType is { } media) part.Headers.ContentType = MediaTypeHeaderValue.Parse(media); content.Add(part, name, upload.FileName); }
        catch { part.Dispose(); throw; }
    }
    /// <summary>明确的原生引用只有 image_url 或 file_id 之一；未知附属字段仍保留。</summary>
    private static void Reference(JsonElement reference)
    {
        if (reference.ValueKind != JsonValueKind.Object) throw new ArgumentException("编辑引用必须是原生对象。");
        var url = reference.TryGetProperty("image_url", out var u); var file = reference.TryGetProperty("file_id", out var f);
        if (url == file) throw new ArgumentException("每个编辑引用必须明确指定唯一 image_url 或 file_id。");
        var source = url ? u : f;
        if (source.ValueKind != JsonValueKind.String) throw new ArgumentException("原生图片引用必须为字符串。");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.GetString());
    }
    /// <summary>上传不查询 Length/Position；相同流不能复用到后续图片或遮罩。</summary>
    private static ImageUpload Upload(ImageUpload upload, HashSet<Stream> sources)
    {
        ArgumentNullException.ThrowIfNull(upload); ArgumentNullException.ThrowIfNull(upload.Content);
        if (!upload.Content.CanRead || !sources.Add(upload.Content)) throw new ArgumentException("图片/遮罩要求独立的可读流。");
        Header(upload.FileName);
        if (upload.ContentType is { } media) { Header(media); _ = MediaTypeHeaderValue.Parse(media); }
        return upload with { };
    }
    /// <summary>调用者公开路由与提示词不可空，不裁剪或推导模型能力。</summary>
    private static void Identity(string model, string prompt)
    { ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(prompt); Text(model); Text(prompt); }
    /// <summary>UTF-8 编码不能静默替换无效代理字符。</summary>
    private static void Text(string value) { ArgumentNullException.ThrowIfNull(value); _ = NativeUtf8.GetByteCount(value); }
    /// <summary>multipart 元数据不能注入头部，但文件名不会作为路径读取。</summary>
    private static void Header(string value)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value); if (value.Any(char.IsControl)) throw new ArgumentException("上传头部含控制字符。"); }
}
