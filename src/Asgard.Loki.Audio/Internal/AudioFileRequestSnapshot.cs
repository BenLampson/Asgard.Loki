namespace Asgard.Loki.Audio.Internal;

/// <summary>复制原生表单元数据，保持流身份及当前位置；不读取、关闭或缓存上传内容。</summary>
internal static class AudioFileRequestSnapshot
{
    /// <summary>校验确定的上传/路由边界，不解析供应商参数或改变原生文本字段。</summary>
    internal static AudioFileRequest Create(AudioFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentNullException.ThrowIfNull(request.File);
        ArgumentNullException.ThrowIfNull(request.File.Content); ArgumentNullException.ThrowIfNull(request.Fields);
        if (!request.File.Content.CanRead) throw new ArgumentException("上传流必须可读。", nameof(request));
        ValidateHeader(request.File.FileName);
        if (request.File.ContentType is not null) ValidateHeader(request.File.ContentType);
        var fields = request.Fields.ToArray();
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); ValidateHeader(field.Name); ArgumentNullException.ThrowIfNull(field.Value);
            if (field.Name is "model" or "file") throw new ArgumentException("原生表单字段不能覆盖已绑定的 model/file。", nameof(request));
        }
        return request with { File = request.File with { }, Fields = Array.AsReadOnly(fields) };
    }

    /// <summary>仅拒绝空白及不合法的头部控制字符；不解释路径、扩展名或本地文件。</summary>
    private static void ValidateHeader(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character => char.IsControl(character))) throw new ArgumentException("表单头部名称/文件元数据不能包含控制字符。");
    }
}
