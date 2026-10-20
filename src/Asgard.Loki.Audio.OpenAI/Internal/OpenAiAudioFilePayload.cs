using System.Net.Http.Headers;
using System.Text;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>快照原生 multipart 字段，原样保留重复名称、顺序、嵌套语法和文本值。</summary>
internal static class OpenAiAudioFilePayload
{
    private static readonly Encoding NativeUtf8 = new UTF8Encoding(false, true);
    /// <summary>直接适配器入口也校验借用上传，不依赖调用者一定经过 Audio 路由。</summary>
    internal static (AudioFileRequest Request, string? Format) Snapshot(AudioFileRequest request, CancellationToken token, bool streaming = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentNullException.ThrowIfNull(request.File);
        ArgumentNullException.ThrowIfNull(request.File.Content); ArgumentNullException.ThrowIfNull(request.Fields);
        if (!request.File.Content.CanRead) throw new ArgumentException("文件上传必须可读。");
        Header(request.File.FileName);
        if (request.File.ContentType is { } media) { Header(media); _ = MediaTypeHeaderValue.Parse(media); }
        var fields = request.Fields.ToArray(); var scalars = new HashSet<string>(StringComparer.Ordinal); string? format = "json";
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value);
            if (field.Name is "model" or "file") throw new ArgumentException("原生字段不能覆盖已绑定的 model/file。");
            if (field.Name is "response_format" or "stream")
            {
                if (!scalars.Add(field.Name)) throw new ArgumentException("原生输出格式和传输开关不能重复。");
                if (field.Name == "response_format") { ArgumentException.ThrowIfNullOrWhiteSpace(field.Value); format = field.Value; }
                else if (field.Value is not ("true" or "false")) throw new ArgumentException("原生 stream 文本必须为 true 或 false。");
                else if (streaming && field.Value != "true") throw new ArgumentException("流式入口不能静默覆盖明确的 stream=false。");
                else if (!streaming && field.Value == "true") throw new NotSupportedException("原生 stream=true 必须使用转录流式入口，不能降级为文件结果。");
            }
        }
        if (streaming && !scalars.Contains("stream")) fields = [.. fields, new("stream", "true")];
        return (request with { File = request.File with { }, Fields = Array.AsReadOnly(fields) }, format);
    }

    /// <summary>只绑定实际模型，主文件位于文本扩展字段之前；不注入格式、流式或采样默认值。</summary>
    internal static MultipartFormDataContent Create(AudioFileRequest request, string model)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(new StringContent(model, NativeUtf8), "model");
            var file = new BorrowedStreamContent(request.File.Content);
            if (request.File.ContentType is { } media) file.Headers.ContentType = MediaTypeHeaderValue.Parse(media);
            content.Add(file, "file", request.File.FileName);
            foreach (var field in request.Fields) content.Add(new StringContent(field.Value, NativeUtf8), field.Name);
            return content;
        }
        catch { content.Dispose(); throw; }
    }

    /// <summary>防止表单头部注入，但不将文件名当路径打开或修改未知参数名称。</summary>
    private static void Header(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("原生表单头部不能包含控制字符。");
        _ = NativeUtf8.GetByteCount(value);
    }
}
