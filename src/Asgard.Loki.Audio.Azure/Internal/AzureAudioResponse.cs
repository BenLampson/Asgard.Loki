using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Azure.Internal;

/// <summary>原生 HTTP 成功与媒体类型边界，不推测模型质量或音频用量。</summary>
internal static class AzureAudioResponse
{
    internal static JsonObject Metadata(HttpResponseMessage response, AzureSpeechPayload request, AzureAudioOptions options)
    {
        var headers = new JsonObject();
        foreach (var field in response.Headers.Concat(response.Content.Headers))
            headers[field.Key] = new JsonArray(field.Value.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return new() { ["kind"] = "azure.audio.http", ["public_model"] = request.PublicModel, ["deployment"] = options.Deployment,
            ["api_version"] = options.ApiVersion, ["status_code"] = (int)response.StatusCode, ["headers"] = headers };
    }
    internal static JsonElement Frame(JsonObject metadata, long count, bool completed)
    {
        var data = metadata.DeepClone().AsObject(); data["bytes_received"] = count; data["completed"] = completed; return JsonSerializer.SerializeToElement(data);
    }
    internal static bool Binary(string? type) => type is not null && (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));
    internal static void Format(string? type, string? requested)
    {
        var actual = type?.ToLowerInvariant() switch { "audio/mpeg" or "audio/mp3" => "mp3", "audio/wav" or "audio/x-wav" => "wav",
            "audio/pcm" => "pcm", "audio/opus" => "opus", "audio/flac" or "audio/x-flac" => "flac", "audio/aac" => "aac", _ => null };
        if (requested is not null && actual is not null && actual != requested) throw new InvalidDataException("Azure 音频媒体类型与显式格式冲突。");
    }
}
