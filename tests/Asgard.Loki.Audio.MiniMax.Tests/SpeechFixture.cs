using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>只从独立原生资源读取预期请求和完整响应。</summary>
internal static class SpeechFixture
{
    internal static JsonElement Root { get; } = Read();
    private static JsonElement Read()
    {
        using var stream = typeof(SpeechFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.MiniMax.Tests.Fixtures.minimax-speech.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static NativeSpeechRequest Request(int index = 0, Action<JsonObject>? edit = null)
    {
        var body = JsonNode.Parse(Root.GetProperty("requests")[index].GetRawText())!.AsObject(); body.Remove("model"); edit?.Invoke(body);
        using var document = JsonDocument.Parse(body.ToJsonString());
        return new() { Model = "sdk-route", Parameters = document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) };
    }
    internal static HttpResponseMessage Response(string name = "hex")
        => JsonResponse(Root.GetProperty("responses").GetProperty(name).GetRawText());
    internal static HttpResponseMessage JsonResponse(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    internal static MiniMaxSpeechOptions Options(int request = 16384, int response = 16384, int audio = 8192)
        => new() { Model = "fixture-native-speech", ApiKey = "fixture-native-access", MaxRequestBytes = request, MaxResponseBytes = response, MaxAudioBytes = audio };
    internal static async Task<List<SpeechChunk>> Collect(IAsyncEnumerable<SpeechChunk> stream)
    { var chunks = new List<SpeechChunk>(); await foreach (var chunk in stream) chunks.Add(chunk); return chunks; }
}
