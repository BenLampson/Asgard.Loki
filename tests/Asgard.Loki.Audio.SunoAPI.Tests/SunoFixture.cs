using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>先于实现提交的独立原生样本，所有身份与媒体均为虚构。</summary>
internal static class SunoFixture
{
    internal static JsonElement Data()
    {
        using var stream = typeof(SunoFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Audio.SunoAPI.Tests.Fixtures.suno-native-audio.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static JsonElement Part(string name) => Data().GetProperty(name);
    internal static JsonObject Object(string name) => JsonNode.Parse(Part(name).GetRawText())!.AsObject();
    internal static AudioTaskRequest Request(int index = 0)
    {
        var item = Part("requests")[index];
        return new() { Model = "music", Operation = item.GetProperty("operation").GetString()!, Parameters = JsonNode.Parse(item.GetProperty("body").GetRawText())!.AsObject() };
    }
    internal static SunoAudioOptions Options(string? model = "fixture-chirp-version", string key = "fixture-proxy-secret",
        SunoAudioAuthentication auth = SunoAudioAuthentication.Bearer, string root = "https://suno-proxy.example.invalid/prefix/",
        int requestLimit = 4 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024, string client = "suno-test")
        => new() { Id = "suno-target", BaseAddress = new(root, UriKind.RelativeOrAbsolute), Authentication = auth, ApiKey = key, MusicModel = model,
            HttpClientName = client, MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    internal static ILokiClient Build(IHttpClientFactory factory, SunoAudioOptions? options = null) => new LokiClientBuilder().AddAudio(new()
    { TaskRoutes = [new() { Model = "music", Targets = [new SunoAudioAdapter(factory, options ?? Options())] }] }).Build();
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => Json(data.GetRawText(), status);
    internal static HttpResponseMessage Json(string data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Json(JsonNode data, HttpStatusCode status = HttpStatusCode.OK) => Json(data.ToJsonString(), status);
    internal static AudioTaskQuery Music => new("task-music-01", "MUSIC");
    internal static AudioTaskQuery Lyrics => new("task-lyrics-02", "LYRICS");
}
