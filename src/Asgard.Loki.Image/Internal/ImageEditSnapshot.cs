using System.Text.Json;

namespace Asgard.Loki.Image.Internal;

/// <summary>分别复制原生 JSON/表单元数据，不读取、关闭、回卷或转换调用者的图片/遮罩内容。</summary>
internal static class ImageEditSnapshot
{
    /// <summary>JSON 模式保留引用/未知数据，不补造供应商字段或 JSON 与 multipart 转换。</summary>
    internal static ImageEditRequest Create(ImageEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key is "model" or "prompt" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("编辑扩展参数不能覆盖 model/prompt 或含 Undefined。");
        return request with { Parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
    }
    /// <summary>保留图片顺序及独立遮罩；重复使用同一流会产生空尾部，必须在派发前拒绝。</summary>
    internal static ImageMultipartEditRequest Create(ImageMultipartEditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); Identity(request.Model, request.Prompt);
        ArgumentNullException.ThrowIfNull(request.Images); ArgumentNullException.ThrowIfNull(request.Fields);
        var sources = new HashSet<Stream>(ReferenceEqualityComparer.Instance); var images = request.Images.Select(image => Upload(image, sources)).ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(images.Length);
        var mask = request.Mask is null ? null : Upload(request.Mask, sources); var fields = request.Fields.ToArray();
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value);
            if (field.Name is "model" or "prompt" or "image" or "image[]" or "images" or "mask")
                throw new ArgumentException("multipart 扩展字段不能覆盖已绑定的模型、提示词、图片或遮罩。");
        }
        return request with { Images = Array.AsReadOnly(images), Mask = mask, Fields = Array.AsReadOnly(fields) };
    }
    /// <summary>公共身份不裁剪提示词或绑定供应商模型目录。</summary>
    private static void Identity(string model, string prompt)
    { ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(prompt); }
    /// <summary>只查看 CanRead；图片编码、尺寸、遮罩语义均留给具体供应商。</summary>
    private static ImageUpload Upload(ImageUpload upload, HashSet<Stream> sources)
    {
        ArgumentNullException.ThrowIfNull(upload); ArgumentNullException.ThrowIfNull(upload.Content);
        if (!upload.Content.CanRead) throw new ArgumentException("图片/遮罩上传必须可读。");
        if (!sources.Add(upload.Content)) throw new ArgumentException("每个图片/遮罩 part 必须使用独立的流对象，SDK 不隐式回卷。");
        Header(upload.FileName); if (upload.ContentType is not null) Header(upload.ContentType); return upload with { };
    }
    /// <summary>头部元数据不允许控制字符，不将文件名当作路径访问。</summary>
    private static void Header(string value)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value); if (value.Any(char.IsControl)) throw new ArgumentException("上传元数据/表单字段名称不能包含控制字符。"); }
}
