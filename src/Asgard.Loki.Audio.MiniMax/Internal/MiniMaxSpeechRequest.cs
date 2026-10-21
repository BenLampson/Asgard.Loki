using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.MiniMax.Internal;

/// <summary>调用前冻结完整请求，公共路由与实际模型严格分离。</summary>
internal sealed record MiniMaxSpeechRequest(byte[] Bytes, bool UrlOutput)
{
    /// <summary>严格绑定模型，原生输出模式保持显式选择。</summary>
    internal static MiniMaxSpeechRequest Prepare(NativeSpeechRequest request, MiniMaxSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); MiniMaxSpeechJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var member in request.Parameters) { MiniMaxSpeechJson.Text(member.Key); MiniMaxSpeechJson.Validate(member.Value); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Parameters);
        if (bytes.Length > options.MaxRequestBytes) throw new InvalidDataException("MiniMax 语音请求超过上限。");
        var root = JsonNode.Parse(MiniMaxSpeechJson.Parse(bytes).GetRawText())!.AsObject();
        MiniMaxSpeechJson.Aliases(root, "model", "text", "stream", "output_format", "voice_setting", "audio_setting");
        if (root.ContainsKey("model")) throw new ArgumentException("根 model 不允许覆盖固定目标。");
        root["model"] = options.Model;
        var native = MiniMaxSpeechJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        _ = MiniMaxSpeechJson.Required(native, "text");
        if (native.TryGetProperty("stream", out var stream))
        {
            if (stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("stream 必须是布尔值。");
            if (stream.GetBoolean()) throw new NotSupportedException("此 MiniMax 语音入口只支持完整原生 JSON，不改写流式请求。");
        }
        var output = native.TryGetProperty("output_format", out _) ? MiniMaxSpeechJson.Required(native, "output_format") : "hex";
        if (output is not ("hex" or "url")) throw new NotSupportedException("此入口支持原生 hex 或 url 输出。");
        foreach (var name in new[] { "voice_setting", "audio_setting" })
            if (root.TryGetPropertyValue(name, out var node) && node is not JsonObject) throw new ArgumentException(name + " 必须为对象。");
        if (root["voice_setting"] is JsonObject voice) MiniMaxSpeechJson.Aliases(voice, "voice_id");
        if (root["audio_setting"] is JsonObject audio) MiniMaxSpeechJson.Aliases(audio, "format", "sample_rate");
        bytes = JsonSerializer.SerializeToUtf8Bytes(native);
        if (bytes.Length > options.MaxRequestBytes) throw new InvalidDataException("MiniMax 语音请求超过上限。");
        return new(bytes, output == "url");
    }
    /// <summary>只映射公共字段，保留音色及音频扩展。</summary>
    internal static NativeSpeechRequest Bridge(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text); MiniMaxSpeechJson.Text(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice); MiniMaxSpeechJson.Text(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format); MiniMaxSpeechJson.Text(request.Format);
        ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentNullException.ThrowIfNull(request.AudioParameters);
        MiniMaxSpeechJson.Node(request.Parameters); MiniMaxSpeechJson.Node(request.AudioParameters);
        var root = request.Parameters.DeepClone().AsObject(); var audio = request.AudioParameters.DeepClone().AsObject();
        MiniMaxSpeechJson.Aliases(root, "text", "voice_setting", "audio_setting", "output_format");
        if (root.ContainsKey("text") || root.ContainsKey("audio_setting")) throw new ArgumentException("扩展不能覆盖公共文本或音频容器。");
        JsonObject voice;
        if (!root.ContainsKey("voice_setting")) { voice = []; root["voice_setting"] = voice; }
        else voice = root["voice_setting"] as JsonObject ?? throw new ArgumentException("voice_setting 必须为对象。");
        MiniMaxSpeechJson.Aliases(voice, "voice_id"); MiniMaxSpeechJson.Aliases(audio, "format", "sample_rate");
        if (voice.ContainsKey("voice_id") || audio.ContainsKey("format") || audio.ContainsKey("sample_rate"))
            throw new ArgumentException("扩展不能覆盖公共音频字段。");
        if (root.TryGetPropertyValue("output_format", out var output)
            && (output is not JsonValue value || !value.TryGetValue<string>(out var format) || format != "hex"))
            throw new NotSupportedException("公共音频字节入口要求 hex 输出；URL 请使用原生入口。");
        root["text"] = request.Text; voice["voice_id"] = request.Voice; audio["format"] = request.Format;
        if (request.SampleRate is { } rate) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate); audio["sample_rate"] = rate; }
        root["audio_setting"] = audio;
        var native = MiniMaxSpeechJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        return new() { Model = request.Model, Parameters = native.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) };
    }
}
