using Asgard.Loki.Video.Internal;

namespace Asgard.Loki.Video;

/// <summary>显式原生 multipart 视频提交，文本与借用文件流不暗中转换成 JSON。</summary>
public sealed record VideoMultipartTaskRequest
{
    /// <summary>公共视频路由，实际模型由选中的完整目标固定。</summary>
    public required string Model { get; init; }
    /// <summary>原生文本字段；根级 model 保留给完整目标配置。</summary>
    public IReadOnlyList<VideoMultipartField> Fields { get; init; } = [];
    /// <summary>按原始顺序上传的独立借用流；可以为空以表达纯表单提交。</summary>
    public IReadOnlyList<VideoUpload> Uploads { get; init; } = [];
    /// <summary>在等待前复制原生元数据并校验流角色，不读取、复制、回卷或关闭文件内容。</summary>
    public VideoMultipartTaskRequest Snapshot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Model); VideoJson.Text(Model);
        ArgumentNullException.ThrowIfNull(Fields); ArgumentNullException.ThrowIfNull(Uploads);
        var fields = Fields.ToArray(); var uploads = Uploads.ToArray(); var streams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value); VideoJson.Text(field.Value);
            if (field.Name == "model") throw new ArgumentException("视频表单字段不能覆盖配置的实际模型。", nameof(Fields));
        }
        for (var index = 0; index < uploads.Length; index++)
        {
            var upload = uploads[index]; ArgumentNullException.ThrowIfNull(upload); ArgumentNullException.ThrowIfNull(upload.Content);
            Header(upload.Name); Header(upload.FileName); if (upload.ContentType is { } type) Header(type);
            if (upload.Name == "model") throw new ArgumentException("上传字段不能覆盖配置的实际模型。", nameof(Uploads));
            if (!upload.Content.CanRead) throw new ArgumentException("视频素材流必须可读。", nameof(Uploads));
            if (!streams.Add(upload.Content)) throw new ArgumentException("每个视频上传 part 必须使用独立流，SDK 不隐式回卷。", nameof(Uploads));
            uploads[index] = upload with { };
        }
        return this with { Fields = Array.AsReadOnly(fields), Uploads = Array.AsReadOnly(uploads) };
    }
    /// <summary>元数据不允许控制字符或无效 Unicode，具体原生字段规则由供应商校验。</summary>
    private static void Header(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); VideoJson.Text(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("视频上传元数据不能包含控制字符。");
    }
}
