using System.Text;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>原生转录事件样本与完整 SSE 字节，分别使用传输身份和原生分段身份。</summary>
internal static class TranscriptionSseFixture
{
    internal const string Delta = """{"type":"transcript.text.delta","delta":"native"}""";
    internal const string Done = """{"type":"transcript.text.done","text":"native final"}""";
    internal const string Segment = """{"type":"transcript.text.segment","id":"seg_A","start":0,"end":1.5,"speaker":"A","text":"native"}""";
    internal static string Event(string json, string? name = null)
        => (name is null ? "" : $"event: {name}\n") + $"data: {json}\n\n";
    internal static string Wire()
    {
        var events = SpeechFixture.Golden("transcription-stream-events"); var body = ": heartbeat\nid: wire_first\nretry: 123\n";
        for (var index = 0; index < events.GetArrayLength(); index++)
        {
            if (index == 2) body += "id: wire_second\n";
            if (index == 5) body += "id: wire_done\n";
            body += Event(events[index].GetRawText(), index == 1 ? null : events[index].GetProperty("type").GetString());
        }
        return body + ": finished\n\n";
    }
    internal static SpeechReadStream Stream(string body) => new(Encoding.UTF8.GetBytes(body));
    internal static HttpResponseMessage Response(SpeechReadStream stream, long? length = null) => SpeechFixture.Response(stream, "text/event-stream", length);
    internal static async Task<List<AudioTranscriptionEvent>> Collect(IAsyncEnumerable<AudioTranscriptionEvent> source)
    { List<AudioTranscriptionEvent> result = []; await foreach (var item in source) result.Add(item); return result; }
}
