using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Audio.Tests;

/// <summary>仅测试核心契约的原生字段样本，不声称已验证 OpenAI HTTP 编码或真实媒体。</summary>
internal static class AudioFileFixture
{
    internal static AudioFileRequest Request(Stream content) => new()
    {
        Model = "file", File = new() { Content = content, FileName = "录音-fixture.wav", ContentType = "audio/wav" },
        Fields = new AudioMultipartField[]
        {
            new("response_format", "verbose_json"), new("timestamp_granularities[]", "word"), new("timestamp_granularities[]", "segment"),
            new("chunking_strategy[type]", "server_vad"), new("chunking_strategy[threshold]", "0.125"),
            new("prompt", "第一行\nsecond"), new("future", "{\"null\":null,\"enabled\":false}"), new("empty", "")
        }
    };
    internal static ILokiClient Build(StubAudioFileAdapter adapter, bool translation = false)
        => new LokiClientBuilder().AddAudio(translation
            ? new AudioOptions { TranslationRoutes = [new() { Model = "file", Targets = [adapter] }] }
            : new AudioOptions { TranscriptionRoutes = [new() { Model = "file", Targets = [adapter] }] }).Build();
    internal static Task<AudioFileResult> Create(ILokiClient client, AudioFileRequest request, bool translation, CancellationToken token)
        => translation ? client.Audio.Translations.CreateAsync(request, token) : client.Audio.Transcriptions.CreateAsync(request, token);
}
