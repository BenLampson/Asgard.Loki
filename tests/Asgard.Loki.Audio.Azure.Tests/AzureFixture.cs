using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>独立原生资源用于公共 SDK 验证，不用生产序列化器生成预期。</summary>
internal static class AzureFixture
{
    internal static JsonElement Root { get; } = Read();
    private static JsonElement Read()
    {
        using var stream = typeof(AzureFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.Azure.Tests.Fixtures.azure-audio.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static AzureAudioOptions Options(bool bodyModel = false, int request = 16384, int response = 16384, long audio = 65536, long upload = 65536)
        => new() { Id = "azure-native", Endpoint = new("https://fixture-resource.example.invalid/"), Deployment = "fixture-deployment.v1",
            ApiVersion = "2025-04-01-preview", ApiKey = "fixture-independent-audio-key", BodyModel = bodyModel ? "fixture-body-model" : null,
            MaxRequestBytes = request, MaxResponseBytes = response, MaxAudioBytes = audio, MaxUploadBytes = upload, ReadBufferSize = 3 };
    internal static NativeSpeechRequest Speech(int index = 0) => new()
    { Model = "public-speech", Parameters = Root.GetProperty("speech_requests")[index].EnumerateObject().Where(x => x.Name != "model").ToDictionary(x => x.Name, x => x.Value.Clone()) };
    internal static AudioFileRequest File(Stream upload, bool translate = false) => new()
    { Model = "public-file", File = new() { Content = upload, FileName = "original.wav", ContentType = "audio/wav" },
        Fields = translate ? [new("response_format", "srt"), new("prompt", "Original English hint"), new("empty", "")]
            : [new("response_format", "verbose_json"), new("language", "zh"), new("temperature", "0"), new("future[]", "false"), new("future[]", "null")] };
    internal static byte[] Audio => Convert.FromBase64String(Root.GetProperty("audio_base64").GetString()!);
    internal static byte[] Upload => Convert.FromBase64String(Root.GetProperty("upload_base64").GetString()!);
    internal static HttpResponseMessage Response(byte[] bytes, string media = "application/json", HttpStatusCode status = HttpStatusCode.OK)
    {
        var result = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }; result.Content.Headers.ContentType = new(media); return result;
    }
    internal static byte[] Result(bool translate) => Encoding.UTF8.GetBytes(translate ? Root.GetProperty("translation_srt").GetString()! : Root.GetProperty("transcription_response").GetRawText());
    internal static async Task<List<SpeechChunk>> Collect(IAsyncEnumerable<SpeechChunk> stream)
    { var result = new List<SpeechChunk>(); await foreach (var chunk in stream) result.Add(chunk); return result; }
}
