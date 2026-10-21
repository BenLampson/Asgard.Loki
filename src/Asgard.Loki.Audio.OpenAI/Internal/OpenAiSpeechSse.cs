using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>解释原生语音音频增量和完成事件，不复用 LLM 或图像的业务终态。</summary>
internal static class OpenAiSpeechSse
{
    /// <summary>保留原生 JSON、原始 data 文本及 SSE 身份，完成块必须等源事件完整读尽。</summary>
    internal static async IAsyncEnumerable<SpeechChunk> ReadAsync(Stream source, JsonObject metadata, string providerId,
        long? expected, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var counted = new SpeechCountingStream(source, expected);
        SpeechChunk? terminal = null;
        await foreach (var frame in SseReader.ReadAsync(counted, cancellationToken, strictUtf8: true).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(frame.Data); var data = document.RootElement;
            ValidateObject(data);
            if (frame.EventName == "error" || data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                || data.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String && errorType.GetString() == "error")
                throw new SpeechUpstreamException("OpenAI 语音 SSE 返回错误事件。", metadata["status_code"]!.GetValue<int>(), frame.Data);
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw new InvalidDataException("语音 SSE 缺少有效原生 type。");
            var name = type.GetString();
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name)
                throw new InvalidDataException("语音 SSE event 名称与原生 type 不一致。");
            if (terminal is not null) throw new InvalidDataException("语音 SSE 在完成后仍出现数据事件。");
            byte[] audio = [];
            if (name == "speech.audio.delta")
            {
                if (!data.TryGetProperty("audio", out var value) || value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("语音增量缺少原生 Base64 audio 字符串。");
                var encoded = value.GetString()!;
                try { audio = Convert.FromBase64String(encoded); }
                catch (FormatException exception) { throw new InvalidDataException("语音 audio 不是有效 Base64。", exception); }
                if (Convert.ToBase64String(audio) != encoded) throw new InvalidDataException("语音 audio 不是规范 Base64。");
            }
            var completed = name == "speech.audio.done";
            if (completed) ValidateUsage(data);
            var envelope = metadata.DeepClone().AsObject(); envelope["transport"] = "sse";
            envelope["event_name"] = frame.EventName; envelope["event_id"] = frame.EventId; envelope["retry_ms"] = frame.RetryMilliseconds;
            envelope["raw_data"] = frame.Data; envelope["data"] = JsonNode.Parse(data.GetRawText());
            var chunk = new SpeechChunk(audio, completed, JsonSerializer.SerializeToElement(envelope), providerId);
            if (completed) terminal = chunk; else yield return chunk;
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("语音 SSE 在 speech.audio.done 之前结束。");
    }

    /// <summary>拒绝含歧义重复成员的对象，避免已知字段被末值覆盖或未知字段静默合并。</summary>
    private static void ValidateObject(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("语音 SSE 事件必须为原生 JSON 对象。");
        ValidateMembers(data);
    }

    /// <summary>递归只校验成员唯一性，不删除未知对象、数组或原生标量。</summary>
    private static void ValidateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("语音 SSE 含重复 JSON 成员。");
                ValidateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateMembers(item);
    }

    /// <summary>完成用量保持原文，仅检查已知必需容器及非负整数类型，不重新计费或推导总数。</summary>
    private static void ValidateUsage(JsonElement data)
    {
        if (!data.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("语音完成事件缺少原生 usage 对象。");
        foreach (var field in new[] { "input_tokens", "output_tokens", "total_tokens" })
        {
            if (!usage.TryGetProperty(field, out var count) || count.ValueKind != JsonValueKind.Number
                || !System.Numerics.BigInteger.TryParse(count.GetRawText(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var integer) || integer.Sign < 0)
                throw new InvalidDataException("语音原生用量必须为非负 JSON 整数。");
        }
    }
}
