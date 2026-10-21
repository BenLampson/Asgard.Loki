using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Azure.Internal;

/// <summary>调用前冻结原生语音 JSON，部署选择器和可选体模型不从公共路由推断。</summary>
internal sealed record AzureSpeechPayload(byte[] Bytes, string PublicModel, string? Format)
{
    internal static AzureSpeechPayload Prepare(NativeSpeechRequest request, AzureAudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); AzureAudioJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        var values = request.Parameters.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
        foreach (var member in values) { AzureAudioJson.Text(member.Key); AzureAudioJson.Validate(member.Value); }
        AzureAudioJson.Aliases(values.Keys, "model", "input", "voice", "response_format", "stream_format", "speed");
        if (values.ContainsKey("model")) throw new ArgumentException("原生 model 只能由固定 BodyModel 配置提供。");
        if (!values.TryGetValue("input", out var input) || input.ValueKind != JsonValueKind.String) throw new ArgumentException("Azure 语音需要字符串 input。");
        if (!values.TryGetValue("voice", out var voice) || voice.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(voice.GetString()))
            throw new ArgumentException("Azure 部署语音需要非空原生字符串 voice。");
        if (values.TryGetValue("stream_format", out var stream) && stream.ValueKind != JsonValueKind.Null
            && (stream.ValueKind != JsonValueKind.String || stream.GetString() != "audio")) throw new NotSupportedException("此部署语音入口只支持二进制音频，不降级原生 SSE。");
        string? format = null;
        if (values.TryGetValue("response_format", out var output) && output.ValueKind != JsonValueKind.Null)
        {
            if (output.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(output.GetString())) throw new ArgumentException("response_format 必须为非空字符串或 null。");
            format = output.GetString();
        }
        if (values.TryGetValue("speed", out var speed) && speed.ValueKind != JsonValueKind.Null
            && (speed.ValueKind != JsonValueKind.Number || !speed.TryGetDouble(out var number) || !double.IsFinite(number))) throw new ArgumentException("speed 必须为有限原生数值或 null。");
        if (options.BodyModel is { } model) values["model"] = JsonSerializer.SerializeToElement(model);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        if (bytes.Length > options.MaxRequestBytes) throw new InvalidDataException("Azure 语音请求超过字节上限。");
        return new(bytes, request.Model, format);
    }
    internal static NativeSpeechRequest Bridge(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Text); ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format); AzureAudioJson.Text(request.Text); AzureAudioJson.Text(request.Voice); AzureAudioJson.Text(request.Format);
        ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentNullException.ThrowIfNull(request.AudioParameters);
        AzureAudioJson.Node(request.Parameters); AzureAudioJson.Node(request.AudioParameters);
        if (request.SampleRate is not null || request.AudioParameters.Count != 0) throw new NotSupportedException("部署语音没有独立采样率/嵌套音频参数的公共映射。");
        var root = request.Parameters.DeepClone().AsObject();
        AzureAudioJson.Aliases(root.Select(x => x.Key), "model", "input", "voice", "response_format");
        if (new[] { "model", "input", "voice", "response_format" }.Any(root.ContainsKey)) throw new ArgumentException("扩展不能覆盖已映射的语音字段。");
        root["input"] = request.Text; root["voice"] = request.Voice; root["response_format"] = request.Format;
        var json = AzureAudioJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        return new() { Model = request.Model, Parameters = json.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) };
    }
}
