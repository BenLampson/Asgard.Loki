using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.Cloudflare.Internal;

/// <summary>固定上传元数据及 JSON 原生字段，不预读借用流或猜测参数类型。</summary>
internal sealed record CloudflareAudioPayload(string PublicModel, Stream Source, string RawContentType, byte[]? Parameters)
{
    /// <summary>Fields 的 JSON 模式值必须为原生 JSON 文本；原始模式不接受被丢弃的字段。</summary>
    internal static CloudflareAudioPayload Prepare(AudioFileRequest request, CloudflareAudioFileOptions options, bool translate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        if (translate && options.InputFormat == CloudflareAudioInputFormat.RawBinary)
            throw new NotSupportedException("原始字节结构没有显式翻译参数；翻译需选择支持 task 的 JSON/Base64 原生结构。");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); CloudflareAudioJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.File); ArgumentNullException.ThrowIfNull(request.File.Content); ArgumentNullException.ThrowIfNull(request.Fields);
        if (!request.File.Content.CanRead) throw new ArgumentException("音频上传源必须可读。");
        Header(request.File.FileName);
        var contentType = request.File.ContentType ?? "application/octet-stream";
        Header(contentType);
        if (!MediaTypeHeaderValue.TryParse(contentType, out _)) throw new ArgumentException("音频媒体类型无效。");
        var fields = request.Fields.ToArray();
        if (options.InputFormat == CloudflareAudioInputFormat.RawBinary)
        {
            if (fields.Length != 0) throw new NotSupportedException("原始音频结构不能发送额外字段。");
            return new(request.Model, request.File.Content, contentType, null);
        }
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long total = 0;
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Header(field.Name); ArgumentNullException.ThrowIfNull(field.Value);
            total += (long)CloudflareAudioJson.TextBytes(field.Name) + CloudflareAudioJson.TextBytes(field.Value);
            if (total > options.MaxParameterBytes) throw new InvalidDataException("Cloudflare 原生音频参数超过上限。");
            if (new[] { "model", "file", "audio" }.Any(name => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("原生参数不能覆盖已绑定的模型或上传音频。");
            if (new[] { "task", "stream" }.Any(name => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && field.Name != name))
                throw new ArgumentException("原生任务字段不接受大小写别名。");
            if (!values.TryAdd(field.Name, CloudflareAudioJson.Parse(Encoding.UTF8.GetBytes(field.Value), false)))
                throw new ArgumentException("原生 JSON 字段不能重复。");
        }
        var task = translate ? "translate" : "transcribe";
        if (values.TryGetValue("task", out var original) && (original.ValueKind != JsonValueKind.String || original.GetString() != task))
            throw new ArgumentException("原生 task 与本次显式操作不一致。");
        values["task"] = JsonSerializer.SerializeToElement(task);
        if (values.TryGetValue("stream", out var streaming) && streaming.ValueKind != JsonValueKind.False)
            throw new NotSupportedException("此音频入口不支持流式响应。");
        var parameters = JsonSerializer.SerializeToUtf8Bytes(values);
        if (parameters.Length > options.MaxParameterBytes) throw new InvalidDataException("Cloudflare 原生音频参数超过上限。");
        return new(request.Model, request.File.Content, contentType, parameters);
    }
    private static void Header(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); CloudflareAudioJson.Text(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("音频字段或元数据名称不能含控制字符。");
    }
}
