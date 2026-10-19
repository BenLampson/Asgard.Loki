using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>记录 SDK 二进制 HTTP 元数据，不伪造供应商 JSON/SSE 音频或用量事件。</summary>
internal static class OpenAiSpeechTransport
{
    /// <summary>保留 HTTP 响应及内容头的完整多值，不记录请求认证信息。</summary>
    internal static JsonObject Metadata(HttpResponseMessage response, string publicModel, string targetModel)
    {
        var headers = new JsonObject();
        foreach (var field in response.Headers.Concat(response.Content.Headers))
            headers[field.Key] = new JsonArray(field.Value.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return new() { ["transport"] = "http_audio", ["public_model"] = publicModel, ["target_model"] = targetModel,
            ["status_code"] = (int)response.StatusCode, ["headers"] = headers };
    }

    /// <summary>仅构造明确标识的传输记录；音频字节独立存储在 SpeechChunk.Audio。</summary>
    internal static JsonElement Frame(JsonObject metadata, long received, bool completed)
    {
        var data = metadata.DeepClone().AsObject(); data["bytes_received"] = received; data["completed"] = completed;
        return JsonSerializer.SerializeToElement(data);
    }

    /// <summary>接受原生二进制媒体类型；已知且明确冲突的格式不被错误标记为请求格式。</summary>
    internal static void ValidateContentType(string? mediaType, string? requestedFormat)
    {
        if (mediaType is null || !(mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("语音成功响应没有原生二进制音频 Content-Type。");
        var format = mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3", "audio/aac" or "audio/x-aac" => "aac",
            "audio/flac" or "audio/x-flac" => "flac", "audio/opus" => "opus",
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "wav", "audio/pcm" => "pcm", _ => null
        };
        if (requestedFormat is not null && format is not null && format != requestedFormat)
            throw new InvalidDataException("原生响应 Content-Type 与明确输出格式不一致。");
    }
}
