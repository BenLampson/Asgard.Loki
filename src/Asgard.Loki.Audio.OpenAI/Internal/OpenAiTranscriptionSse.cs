using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>原生转录事件传输，不执行文本合并、说话人识别、重连或 LLM/音频工作流。</summary>
internal static class OpenAiTranscriptionSse
{
    /// <summary>保留完整原生 JSON/SSE 身份；终态等待正常 EOF，外层还必须成功释放传输资源。</summary>
    internal static async IAsyncEnumerable<AudioTranscriptionEvent> ReadAsync(Stream source, long? expected, AudioResultMetadata metadata,
        int status, string? contentType, int maximum, [EnumeratorCancellation] CancellationToken token)
    {
        using var counted = new SpeechCountingStream(source, expected); var state = new OpenAiTranscriptionState(maximum);
        AudioTranscriptionEvent? terminal = null;
        await foreach (var frame in SseReader.ReadAsync(counted, token, strictUtf8: true).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(frame.Data); var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生转录事件必须为 JSON 对象。");
            if (frame.EventName == "error" || data.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)
                || data.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String && errorType.GetString() == "error")
                throw new AudioFileUpstreamException("OpenAI 原生转录流返回错误。", status, Encoding.UTF8.GetBytes(frame.Data), contentType)
                { TranscriptionEvent = new(data, false, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds) };
            OpenAiAudioFileResponse.UniqueMembers(data);
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw new InvalidDataException("原生转录事件缺少有效 type。");
            var name = type.GetString()!;
            if (frame.EventName is { Length: > 0 } eventName && eventName != "message" && eventName != name)
                throw new InvalidDataException("转录 SSE event 与原生 type 冲突。");
            if (terminal is not null) throw new InvalidDataException("转录完成后仍出现数据事件。");
            var done = state.Validate(data, name);
            var item = new AudioTranscriptionEvent(data, done, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
            if (done) terminal = item; else yield return item;
        }
        token.ThrowIfCancellationRequested();
        yield return terminal ?? throw new InvalidDataException("转录 SSE 在 transcript.text.done 之前结束。");
    }
}
