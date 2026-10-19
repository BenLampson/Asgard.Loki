using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>保留原生请求字段与 voice 对象；便捷请求只映射有明确对应的字段。</summary>
internal static class OpenAiSpeechPayload
{
    /// <summary>便捷接口不猜测采样率或丢弃 AudioParameters；无对应结构时明确拒绝。</summary>
    internal static (JsonObject Body, string? Format, string PublicModel) Create(SpeechRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text); ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format); ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentNullException.ThrowIfNull(request.AudioParameters);
        if (request.SampleRate is <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.SampleRate is not null || request.AudioParameters.Count != 0)
            throw new NotSupportedException("OpenAI 语音没有本接口的独立采样率/嵌套音频参数映射，请使用明确原生字段。");
        if (new[] { "model", "input", "voice", "response_format" }.Any(request.Parameters.ContainsKey))
            throw new ArgumentException("语音扩展参数不能覆盖便捷接口已绑定的字段。");
        var body = request.Parameters.DeepClone().AsObject(); body["input"] = request.Text; body["voice"] = request.Voice; body["response_format"] = request.Format;
        return Validate(body, request.Model, model);
    }

    /// <summary>原生入口保留字段省略/null，不把原生声音对象降为字符串。</summary>
    internal static (JsonObject Body, string? Format, string PublicModel) Create(NativeSpeechRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生语音参数不能覆盖根 model 或包含 Undefined。");
        var snapshot = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        return Validate(JsonSerializer.SerializeToNode(snapshot)!.AsObject(), request.Model, model);
    }

    /// <summary>校验已知容器和二进制传输模式，不查询模型/声音列表或补造默认参数。</summary>
    private static (JsonObject Body, string? Format, string PublicModel) Validate(JsonObject body, string publicModel, string model)
    {
        if (body["input"] is not JsonValue input || input.GetValueKind() != JsonValueKind.String) throw new ArgumentException("原生语音需要字符串 input。");
        if (body["voice"] is JsonObject voice)
        {
            if (voice["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetValue<string>()))
                throw new ArgumentException("自定义 voice 需要非空原生 id 字符串。");
        }
        else if (body["voice"] is not JsonValue name || name.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetValue<string>()))
            throw new ArgumentException("原生 voice 必须为非空字符串或自定义声音对象。");
        if (body["stream_format"] is { } stream)
        {
            if (stream.GetValueKind() != JsonValueKind.String) throw new ArgumentException("stream_format 必须为原生字符串或 null。");
            if (stream.GetValue<string>() != "audio") throw new NotSupportedException("当前语音适配器只实现原生二进制 audio 传输，SSE 尚未接入。");
        }
        string? format = body.ContainsKey("response_format") ? null : "mp3";
        if (body["response_format"] is { } formatValue)
        {
            if (formatValue.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(formatValue.GetValue<string>()))
                throw new ArgumentException("response_format 必须为非空字符串或 null。");
            format = formatValue.GetValue<string>();
        }
        if (body["instructions"] is { } instructions && instructions.GetValueKind() != JsonValueKind.String)
            throw new ArgumentException("instructions 必须为字符串或 null。");
        if (body["speed"] is { } speed)
        {
            var number = JsonSerializer.SerializeToElement(speed);
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new ArgumentException("speed 必须为有限原生数值或 null。");
        }
        body["model"] = model; return (body, format, publicModel);
    }
}
