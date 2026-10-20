using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>调用前冻结原生请求；目标身份固定，原生内容及未知字段保留。</summary>
internal sealed record V1SpeechRequest(byte[] Bytes, string RequestId)
{
    /// <summary>原生请求保持四个原始容器，不发送 SDK 路由名。</summary>
    internal static V1SpeechRequest Prepare(NativeSpeechRequest request, VolcengineV1SpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); V1SpeechJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var member in request.Parameters) { V1SpeechJson.Text(member.Key); V1SpeechJson.Validate(member.Value); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Parameters);
        if (bytes.Length > options.MaxRequestBytes) throw new InvalidDataException("V1 请求超过字节上限。");
        var root = JsonNode.Parse(V1SpeechJson.Parse(bytes).GetRawText())!.AsObject();
        V1SpeechJson.Aliases(root, "app", "user", "audio", "request", "model");
        if (root.ContainsKey("model")) throw new ArgumentException("根 model 保留给 SDK 路由。");
        var app = Container(root, "app"); var user = Container(root, "user");
        var audio = Container(root, "audio"); var native = Container(root, "request");
        V1SpeechJson.Aliases(app, "appid", "token", "cluster");
        V1SpeechJson.Aliases(user, "uid");
        V1SpeechJson.Aliases(audio, "voice_type", "encoding", "rate");
        V1SpeechJson.Aliases(native, "reqid", "text", "operation", "model");
        Bind(app, "appid", options.AppId); Bind(app, "cluster", options.Cluster);
        Bind(native, "operation", options.Transport == VolcengineV1SpeechTransport.Http ? "query" : "submit");
        var checkedRoot = V1SpeechJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        _ = V1SpeechJson.Required(checkedRoot.GetProperty("app"), "token");
        _ = V1SpeechJson.Required(checkedRoot.GetProperty("user"), "uid");
        _ = V1SpeechJson.Required(checkedRoot.GetProperty("audio"), "voice_type");
        var settings = checkedRoot.GetProperty("request");
        _ = V1SpeechJson.Required(settings, "text"); var requestId = V1SpeechJson.Required(settings, "reqid");
        if (audio.TryGetPropertyValue("encoding", out var encoding))
        {
            if (encoding is not JsonValue value || !value.TryGetValue<string>(out var format) || string.IsNullOrWhiteSpace(format))
                throw new ArgumentException("V1 encoding 必须是非空字符串。");
            if (options.Transport == VolcengineV1SpeechTransport.WebSocket && format.Equals("wav", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("V1 WebSocket 不支持 wav 流式输出。");
        }
        if (audio.TryGetPropertyValue("rate", out var rate) && (rate is not JsonValue number || !number.TryGetValue<int>(out var sampleRate) || sampleRate <= 0))
            throw new ArgumentException("V1 rate 必须是正整数。");
        bytes = JsonSerializer.SerializeToUtf8Bytes(checkedRoot);
        if (bytes.Length > options.MaxRequestBytes) throw new InvalidDataException("V1 请求超过字节上限。");
        return new(bytes, requestId);
    }

    /// <summary>公共音频字段映射；身份、请求标识和其他原生设置仍由调用者提供。</summary>
    internal static NativeSpeechRequest Bridge(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text); V1SpeechJson.Text(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice); V1SpeechJson.Text(request.Voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format); V1SpeechJson.Text(request.Format);
        ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentNullException.ThrowIfNull(request.AudioParameters);
        V1SpeechJson.ValidateNode(request.Parameters); V1SpeechJson.ValidateNode(request.AudioParameters);
        var root = request.Parameters.DeepClone().AsObject(); var audio = request.AudioParameters.DeepClone().AsObject();
        V1SpeechJson.Aliases(root, "audio", "request");
        if (root.ContainsKey("audio")) throw new ArgumentException("公共请求的 audio 必须通过 AudioParameters 提供。");
        V1SpeechJson.Aliases(audio, "voice_type", "encoding", "rate");
        if (audio.ContainsKey("voice_type") || audio.ContainsKey("encoding") || audio.ContainsKey("rate"))
            throw new ArgumentException("AudioParameters 不能覆盖公共音频字段。");
        var native = Container(root, "request"); V1SpeechJson.Aliases(native, "text");
        if (native.ContainsKey("text")) throw new ArgumentException("原生 request.text 不能覆盖公共 Text。");
        native["text"] = request.Text; audio["voice_type"] = request.Voice; audio["encoding"] = request.Format;
        if (request.SampleRate is { } rate) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate); audio["rate"] = rate; }
        root["audio"] = audio;
        var parsed = V1SpeechJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        return new NativeSpeechRequest { Model = request.Model, Parameters = parsed.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) };
    }

    private static JsonObject Container(JsonObject root, string name)
        => root[name] as JsonObject ?? throw new ArgumentException("V1 请求缺少对象 " + name + "。");
    private static void Bind(JsonObject obj, string name, string expected)
    {
        if (!obj.ContainsKey(name)) { obj[name] = expected; return; }
        if (obj[name] is not JsonValue value || !value.TryGetValue<string>(out var text) || text != expected)
            throw new ArgumentException("V1 请求不能覆盖固定目标或传输字段 " + name + "。");
    }
}
