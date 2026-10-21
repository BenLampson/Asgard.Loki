using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>JSON 生成和原生 multipart 编辑分别快照，不下载引用或处理图像像素。</summary>
internal static class AzureImagePayload
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    /// <summary>公开提示词与路由不被原生扩展字段覆盖；JSON 在创建客户端前编码。</summary>
    internal static AzureImageGenerationPayload Generation(ImageGenerationRequest request, AzureImageOptions options, CancellationToken token, bool streaming = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in request.Parameters)
        {
            AzureImageJson.Text(field.Key); AzureImageJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Azure 图像扩展不能覆盖绑定的 model/prompt。");
            if (field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Key != "stream" || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("原生 stream 必须使用准确字段名和布尔值。");
                if (streaming && field.Value.ValueKind == JsonValueKind.False) throw new ArgumentException("流式生成不能覆盖显式 stream=false。");
                if (!streaming && field.Value.ValueKind == JsonValueKind.True) throw new NotSupportedException("stream=true 必须使用原生 StreamAsync 入口。");
            }
            if (streaming && (field.Key.Equals("n", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("partial_images", StringComparison.OrdinalIgnoreCase))
                && field.Key is not ("n" or "partial_images")) throw new ArgumentException("流计数字段不能使用大小写别名。");
            copy.Add(field.Key, field.Value.Clone());
        }
        var expected = 1;
        if (streaming)
        {
            if (copy.TryGetValue("n", out var count)) expected = AzureImageNumber.Small(count, 1, 10);
            if (copy.TryGetValue("partial_images", out var partial)) _ = AzureImageNumber.Small(partial, 0, 3);
            if (!copy.ContainsKey("stream")) copy.Add("stream", JsonSerializer.SerializeToElement(true));
        }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); if (options.BodyModel is { } model) writer.WriteString("model", model); writer.WriteString("prompt", request.Prompt);
            foreach (var field in copy) { writer.WritePropertyName(field.Key); field.Value.WriteTo(writer); } writer.WriteEndObject();
        }
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Azure 图像 JSON 请求超过本地字节上限。");
        return new(request.Model, bytes.ToArray(), expected);
    }
    /// <summary>每次枚举单独创建已冻结正文的内容，未枚举时不持有 HTTP 资源。</summary>
    internal static HttpContent Json(byte[] bytes)
    { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/json"); return content; }
    /// <summary>元数据/字段独立复制，图片及遮罩流仅借用；不读长度或回卷。</summary>
    internal static ImageMultipartEditRequest Snapshot(ImageMultipartEditRequest request, AzureImageOptions options, CancellationToken token, bool streaming = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Images); ArgumentNullException.ThrowIfNull(request.Fields);
        long bytes = (long)AzureImageJson.Text(request.Prompt) + AzureImageJson.Text(options.BodyModel ?? "");
        var sources = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        ImageUpload Upload(ImageUpload value)
        {
            ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Content); Header(value.FileName);
            if (!value.Content.CanRead || !sources.Add(value.Content)) throw new ArgumentException("图片及遮罩必须使用各自独立的可读借用流。");
            bytes += AzureImageJson.Text(value.FileName);
            if (value.ContentType is { } type)
            {
                Header(type); if (!MediaTypeHeaderValue.TryParse(type, out _)) throw new ArgumentException("图像上传 Content-Type 无效。");
                bytes += AzureImageJson.Text(type);
            }
            return value with { };
        }
        var images = request.Images.Select(Upload).ToArray(); ArgumentOutOfRangeException.ThrowIfZero(images.Length);
        var mask = request.Mask is null ? null : Upload(request.Mask); var fields = request.Fields.ToArray(); var single = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value); AzureImageJson.Text(field.Value);
            var name = field.Name.ToLowerInvariant();
            if (name is "model" or "prompt" or "image" or "images" or "mask" || name.StartsWith("image[", StringComparison.Ordinal)
                || name.StartsWith("images[", StringComparison.Ordinal) || name.StartsWith("mask[", StringComparison.Ordinal))
                throw new ArgumentException("表单扩展不能覆盖绑定模型、提示词、图片或遮罩。");
            if (name is "n" or "stream" or "response_format" or "background" or "input_fidelity" or "output_compression"
                or "output_format" or "quality" or "size" or "user" or "partial_images" or "moderation")
            {
                if (field.Name != name || !single.Add(name)) throw new ArgumentException("已知原生表单单值字段不能重复或使用大小写别名。");
            }
            if (name == "stream")
            {
                if (field.Value is not ("true" or "false")) throw new ArgumentException("原生表单 stream 必须为 true/false 文本。");
                if (streaming && field.Value == "false") throw new ArgumentException("流式编辑不能覆盖显式 stream=false。");
                if (!streaming && field.Value == "true") throw new NotSupportedException("stream=true 必须使用 StreamMultipartEditAsync 入口。");
            }
            if (streaming && name is "n" or "partial_images") _ = AzureImageNumber.Text(field.Value, name == "n" ? 1 : 0, name == "n" ? 10 : 3);
            bytes += AzureImageJson.Text(field.Name) + AzureImageJson.Text(field.Value);
        }
        if (streaming && !single.Contains("stream")) { fields = [.. fields, new("stream", "true")]; bytes += AzureImageJson.Text("streamtrue"); }
        if (bytes > options.MaxRequestBytes) throw new ArgumentException("Azure 编辑文本/元数据超过本地字节上限。");
        return request with { Images = Array.AsReadOnly(images), Mask = mask, Fields = Array.AsReadOnly(fields) };
    }
    /// <summary>原生 n 省略时采用文档的单图终态约束，但不向请求写入默认 n。</summary>
    internal static int ExpectedImages(ImageMultipartEditRequest request)
        => request.Fields.SingleOrDefault(field => field.Name == "n") is { } field ? AzureImageNumber.Text(field.Value, 1, 10) : 1;
    /// <summary>单图 image，多图 image[]，mask 单独保留；不插入原生默认参数。</summary>
    internal static HttpContent Multipart(ImageMultipartEditRequest request, AzureImageOptions options)
    {
        var content = new MultipartFormDataContent();
        try
        {
            if (options.BodyModel is { } model) content.Add(new StringContent(model, Utf8), "model");
            content.Add(new StringContent(request.Prompt, Utf8), "prompt");
            void Add(ImageUpload upload, string name)
            {
                var part = new BorrowedStreamContent(upload.Content, options.MaxUploadBytes);
                try { if (upload.ContentType is { } type) part.Headers.ContentType = MediaTypeHeaderValue.Parse(type); content.Add(part, name, upload.FileName); }
                catch { part.Dispose(); throw; }
            }
            foreach (var upload in request.Images) Add(upload, request.Images.Count == 1 ? "image" : "image[]");
            if (request.Mask is { } mask) Add(mask, "mask");
            foreach (var field in request.Fields) content.Add(new StringContent(field.Value, Utf8), field.Name);
            return content;
        }
        catch { content.Dispose(); throw; }
    }
    /// <summary>原生元数据禁止请求头控制字符，但正文换行不受影响。</summary>
    private static void Header(string text)
    { ArgumentException.ThrowIfNullOrWhiteSpace(text); AzureImageJson.Text(text); if (text.Any(char.IsControl)) throw new ArgumentException("图像表单元数据含控制字符。"); }
    /// <summary>公共身份和原提示词均需要有效 Unicode，保持原文不 Trim。</summary>
    private static void Identity(string model, string prompt)
    { ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(prompt); AzureImageJson.Text(model); AzureImageJson.Text(prompt); }
}
