using System.Net.Http.Headers;
using System.Text;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Azure.Internal;

/// <summary>冻结原生 multipart 文本/文件元数据，文件继续从调用者当前位置单次读取。</summary>
internal sealed record AzureAudioFilePayload(AudioFileRequest Request, string? Format)
{
    /// <summary>只验证原生边界，不把 JSON、重复原生字段或调用者文本强制改写为其他类型。</summary>
    internal static AzureAudioFilePayload Prepare(AudioFileRequest request, AzureAudioOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        AzureAudioJson.Text(request.Model); ArgumentNullException.ThrowIfNull(request.File); ArgumentNullException.ThrowIfNull(request.File.Content);
        ArgumentNullException.ThrowIfNull(request.Fields); if (!request.File.Content.CanRead) throw new ArgumentException("Azure 上传源必须可读。");
        Header(request.File.FileName);
        if (request.File.ContentType is { } type) { Header(type); _ = MediaTypeHeaderValue.Parse(type); }
        var fields = request.Fields.ToArray(); var controls = new HashSet<string>(StringComparer.Ordinal); string? format = null;
        long total = options.BodyModel is { } model ? AzureAudioJson.Text(model) : 0;
        if (total > options.MaxRequestBytes) throw new InvalidDataException("Azure 固定请求体模型超过上传参数上限。");
        AzureAudioJson.Aliases(fields.Select(field => field?.Name ?? ""), "model", "file", "stream", "response_format");
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value);
            total += (long)AzureAudioJson.Text(field.Name) + AzureAudioJson.Text(field.Value);
            if (total > options.MaxRequestBytes) throw new InvalidDataException("Azure 非音频上传参数超过上限。");
            if (field.Name is "model" or "file") throw new ArgumentException("上传字段不能覆盖固定模型或文件。");
            if (field.Name is "stream" or "response_format" && !controls.Add(field.Name)) throw new ArgumentException("原生传输或格式字段不能重复。");
            if (field.Name == "stream" && field.Value != "false") throw new NotSupportedException("此部署文件入口不降级原生流式请求。");
            if (field.Name == "response_format") { ArgumentException.ThrowIfNullOrWhiteSpace(field.Value); format = field.Value; }
        }
        return new(request with { File = request.File with { }, Fields = Array.AsReadOnly(fields) }, format);
    }
    /// <summary>只发送调用者已配置的可选体模型和原始 parts，不从公共路由推断 model。</summary>
    internal HttpContent Content(AzureAudioOptions options)
    {
        var content = new MultipartFormDataContent();
        try
        {
            if (options.BodyModel is { } model) content.Add(new StringContent(model, Encoding.UTF8), "model");
            var file = new BorrowedStreamContent(Request.File.Content, options.MaxUploadBytes);
            if (Request.File.ContentType is { } type) file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            content.Add(file, "file", Request.File.FileName);
            foreach (var field in Request.Fields) content.Add(new StringContent(field.Value, Encoding.UTF8), field.Name);
            return content;
        }
        catch { content.Dispose(); throw; }
    }
    private static void Header(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); AzureAudioJson.Text(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("Azure 原生 part 名称或文件元数据含控制字符。");
    }
}
