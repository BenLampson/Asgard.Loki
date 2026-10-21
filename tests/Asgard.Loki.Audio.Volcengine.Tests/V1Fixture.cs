using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Audio.Volcengine;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>从独立资源读取原生样本；不调用生产编码器构造预期报文。</summary>
internal static class V1Fixture
{
    internal static JsonElement Root { get; } = Read();
    private static JsonElement Read()
    {
        using var stream = typeof(V1Fixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.Volcengine.Tests.Fixtures.volcengine-v1-speech.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static byte[] Frame(string name) => Convert.FromBase64String(Root.GetProperty("server_frames").EnumerateArray()
        .Single(x => x.GetProperty("name").GetString() == name).GetProperty("wire_base64").GetString()!);
    internal static JsonObject Body(int index = 0) => JsonNode.Parse(Root.GetProperty("requests")[index].GetProperty("wire_request").GetRawText())!.AsObject();
    internal static NativeSpeechRequest Request(int index = 0, Action<JsonObject>? edit = null)
    {
        var body = Body(index); edit?.Invoke(body);
        using var document = JsonDocument.Parse(body.ToJsonString());
        return new() { Model = "sdk-public-route", Parameters = document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) };
    }
    internal static VolcengineV1SpeechOptions Options(VolcengineV1SpeechTransport transport = VolcengineV1SpeechTransport.WebSocket,
        bool compress = false, int message = 4096, int decoded = 4096, long total = 65536, int frames = 4096, int request = 16384, int timeoutMs = 5000)
        => new() { AppId = "fixture-v1-app", Cluster = "fixture-native-cluster", AccessToken = "fixture-header-access", Transport = transport,
            CompressRequest = compress, MaxMessageBytes = message, MaxDecodedBytes = decoded, MaxResponseBytes = total,
            MaxFrames = frames, MaxRequestBytes = request, CloseTimeout = TimeSpan.FromMilliseconds(timeoutMs) };
    internal static async Task<List<SpeechChunk>> Collect(IAsyncEnumerable<SpeechChunk> stream)
    {
        var chunks = new List<SpeechChunk>(); await foreach (var chunk in stream) chunks.Add(chunk); return chunks;
    }
}
