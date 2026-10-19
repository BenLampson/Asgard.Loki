using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>独立 SSE 样本；数据及终态来自人工协议场景，HTTP 分片不等于事件边界。</summary>
internal static class SpeechSseFixture
{
    internal const string Delta = "{\"type\":\"speech.audio.delta\",\"audio\":\"AP+A\"}";
    internal const string Done = "{\"type\":\"speech.audio.done\",\"usage\":{\"input_tokens\":3,\"output_tokens\":5,\"total_tokens\":8}}";
    internal static NativeSpeechRequest Request() => SpeechFixture.Native("sse-request");
    internal static string Event(string json, string? name = null) => (name is null ? "" : $"event: {name}\n") + $"data: {json}\n\n";
    internal static string Wire()
    {
        var events = SpeechFixture.Golden("sse-events");
        return ": heartbeat\nid: frame_one\nretry: 123\n" + Event(events[0].GetRawText(), "speech.audio.delta")
            + Event(events[1].GetRawText()) + "id: frame_two\n" + Event(events[2].GetRawText(), "speech.audio.delta")
            + "id: frame_done\n" + Event(events[3].GetRawText(), "speech.audio.done") + ": finished\n\n";
    }
    internal static SpeechReadStream Stream(string body) => new(Encoding.UTF8.GetBytes(body));
    internal static HttpResponseMessage Response(SpeechReadStream stream, long? length = null) => SpeechFixture.Response(stream, "text/event-stream", length);
}
