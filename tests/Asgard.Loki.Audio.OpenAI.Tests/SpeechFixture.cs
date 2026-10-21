using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>原生请求快照与不具备媒体编码含义的字节样本；不访问网络或真实凭据。</summary>
internal static class SpeechFixture
{
    internal static readonly byte[] Bytes = [0, 255, 128, 13, 10, 42, 0, 254];
    internal static OpenAiSpeechOptions Options => new()
    {
        Model = "tts-upstream-fixture", ApiKey = "fixture-secret", Id = "openai-speech-one",
        BaseAddress = new("https://speech.invalid/tenant/v1/"), Organization = "org-fixture", Project = "project-fixture", ReadBufferSize = 3
    };
    internal static JsonElement Golden(string name)
    {
        using var stream = typeof(SpeechFixture).Assembly.GetManifestResourceStream($"Asgard.Loki.Audio.OpenAI.Tests.Fixtures.{name}.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static NativeSpeechRequest Native(string fixture = "native") => new()
    {
        Model = "voice", Parameters = Golden(fixture).EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone())
    };
    internal static ILokiClient Build(OpenAiSpeechAdapter adapter) => new LokiClientBuilder().AddAudio(new AudioOptions
    { SpeechRoutes = [new SpeechRoute { Model = "voice", Targets = [adapter] }] }).Build();
    internal static HttpResponseMessage Response(SpeechReadStream stream, string? media = "audio/pcm", long? length = 8, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(stream);
        if (media is not null) content.Headers.ContentType = new MediaTypeHeaderValue(media);
        content.Headers.ContentLength = length;
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.Add("x-request-id", "req_fixture"); response.Headers.Add("x-future", ["one", "two"]); return response;
    }
    internal static async Task<List<SpeechChunk>> Collect(IAsyncEnumerable<SpeechChunk> source)
    { List<SpeechChunk> chunks = []; await foreach (var chunk in source) chunks.Add(chunk); return chunks; }
}
