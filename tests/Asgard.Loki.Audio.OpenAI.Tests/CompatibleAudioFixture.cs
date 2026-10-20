using System.Text.Json;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>独立完整兼容请求与原生结果样本；模型/身份只来自显式配置。</summary>
internal static class CompatibleAudioFixture
{
    internal static JsonElement Root { get; } = Read();
    private static JsonElement Read()
    {
        using var stream = typeof(CompatibleAudioFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.OpenAI.Tests.Fixtures.Compatible.siliconflow-audio.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static OpenAiSpeechOptions SpeechOptions => new()
    { Id = "siliconflow-explicit-tts", Model = "fixture-native-tts", ApiKey = "fixture-compatible-speech", BaseAddress = new("https://api.siliconflow.cn/v1/"), ReadBufferSize = 3 };
    internal static OpenAiAudioFileOptions FileOptions => new()
    { Id = "siliconflow-explicit-asr", Model = "fixture-native-asr", ApiKey = "fixture-compatible-file", BaseAddress = new("https://api.siliconflow.cn/v1/") };
    internal static NativeSpeechRequest Speech(int index = 0) => new()
    { Model = "voice", Parameters = Root.GetProperty("speech_requests")[index].EnumerateObject().Where(x => x.Name != "model").ToDictionary(x => x.Name, x => x.Value.Clone()) };
    internal static AudioFileRequest File(Stream source, bool extended = false) => new()
    { Model = "recording", File = new() { Content = source, FileName = "original.wav", ContentType = "audio/wav" },
        Fields = extended ? [new("language", "zh"), new("future[]", "0"), new("future[]", "false"), new("empty", "")] : [] };
    internal static byte[] Audio => Convert.FromBase64String(Root.GetProperty("audio_base64").GetString()!);
    internal static byte[] Upload => Convert.FromBase64String(Root.GetProperty("file_base64").GetString()!);
}
