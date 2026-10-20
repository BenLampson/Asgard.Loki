using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>独立原生资源与公共调用样本，不通过生产代码生成预期正文。</summary>
internal static class AudioFixture
{
    internal static JsonElement Root { get; } = Read();
    private static JsonElement Read()
    {
        using var stream = typeof(AudioFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.Cloudflare.Tests.Fixtures.cloudflare-audio.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static byte[] Bytes => Convert.FromBase64String(Root.GetProperty("audio_base64").GetString()!);
    internal static AudioFileRequest Request(Stream source, int index = 0)
    {
        var frame = Root.GetProperty("requests")[index];
        return new() { Model = "public-audio", File = new() { Content = source, FileName = "original-native.wav", ContentType = "audio/wav" },
            Fields = index == 0 ? [] : frame.GetProperty("body").EnumerateObject().Where(x => x.Name != "audio")
                .Select(x => new AudioMultipartField(x.Name, x.Value.GetRawText())).ToArray() };
    }
    internal static CloudflareAudioFileOptions Options(CloudflareAudioInputFormat format = CloudflareAudioInputFormat.RawBinary,
        long upload = 65536, int parameters = 16384, int response = 16384)
        => new() { AccountId = "fixture-account", Model = format == CloudflareAudioInputFormat.RawBinary ? "@cf/openai/whisper" : "@cf/openai/whisper-large-v3-turbo",
            ApiToken = "fixture-native-access", InputFormat = format, MaxUploadBytes = upload, MaxParameterBytes = parameters, MaxResponseBytes = response };
    internal static HttpResponseMessage Response(string name = "raw")
        => JsonResponse(Root.GetProperty("responses").GetProperty(name).GetRawText());
    internal static HttpResponseMessage JsonResponse(string text, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
