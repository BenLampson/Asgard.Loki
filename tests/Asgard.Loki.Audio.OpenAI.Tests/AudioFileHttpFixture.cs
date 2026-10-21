using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>独立请求输入及结果样本，通过实际供应商 HTTP 路径而非核心替身执行。</summary>
internal static class AudioFileHttpFixture
{
    internal static readonly byte[] UploadBytes = [0, 255, 128, 13, 10, 42];
    internal static OpenAiAudioFileOptions Options => new()
    {
        Model = "file-upstream-fixture", ApiKey = "fixture-file-secret", Id = "file-target-one",
        BaseAddress = new("https://audio.invalid/tenant/v1/"), Organization = "org-fixture", Project = "project-fixture"
    };
    internal static AudioFileRequest Request(Stream stream, string scenario) => new()
    {
        Model = "recording", File = new() { Content = stream, FileName = "fixture.wav", ContentType = scenario == "minimal" ? null : "audio/wav" },
        Fields = scenario switch
        {
            "rich" => [new("response_format", "verbose_json"), new("stream", "false"), new("language", "zh"), new("temperature", "0.125"),
                new("prompt", "你好\nfixture"), new("timestamp_granularities[]", "word"), new("timestamp_granularities[]", "segment"),
                new("include[]", "logprobs"), new("chunking_strategy[type]", "server_vad"), new("chunking_strategy[threshold]", "0.25"),
                new("future[]", "null"), new("future[]", "false"), new("future", "{\"enabled\":false,\"value\":null}"), new("empty", "")],
            "diarized" => [new("response_format", "diarized_json"), new("chunking_strategy", "auto"), new("known_speaker_names[]", "A"), new("known_speaker_names[]", "B"),
                new("known_speaker_references[]", "data:audio/wav;base64,AAE="), new("known_speaker_references[]", "data:audio/wav;base64,AgM=")],
            "context" => [new("keywords[]", "SDK"), new("keywords[]", "原生"), new("languages[]", "zh"), new("languages[]", "en"), new("prompt", "fixture context")],
            "translation" => [new("response_format", "srt"), new("temperature", "0.0"), new("prompt", "English hint"), new("future", "001.00")], _ => []
        }
    };
    internal static ILokiClient Build(OpenAiAudioFileAdapter adapter, bool translation) => new LokiClientBuilder().AddAudio(translation
        ? new AudioOptions { TranslationRoutes = [new() { Model = "recording", Targets = [adapter] }] }
        : new AudioOptions { TranscriptionRoutes = [new() { Model = "recording", Targets = [adapter] }] }).Build();
    internal static Task<AudioFileResult> Create(ILokiClient client, AudioFileRequest request, bool translation, CancellationToken token)
        => translation ? client.Audio.Translations.CreateAsync(request, token) : client.Audio.Transcriptions.CreateAsync(request, token);
    internal static byte[] ResponseBytes(string scenario) => scenario == "translation" ? Encoding.UTF8.GetBytes("1\r\n00:00:00,125 --> 00:00:01,750\r\nHello 😀\r\n")
        : Encoding.UTF8.GetBytes(SpeechFixture.Golden(scenario == "diarized" ? "file-diarized-response" : "file-json-response").GetRawText());
    internal static HttpResponseMessage Response(byte[] body, string media = "application/json")
        => SpeechFixture.Response(new SpeechReadStream(body), media, body.Length);
}
