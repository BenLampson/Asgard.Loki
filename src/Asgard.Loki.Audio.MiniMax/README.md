# Asgard.Loki.Audio.MiniMax

MiniMax native synchronous HTTP speech synthesis for the independent Audio capability.

| Contract | Support |
|---|---|
| `POST /v1/t2a_v2`, original native JSON | Supported through `INativeSpeechAdapter` |
| Complete hex audio | Decoded ordered audio bytes plus complete native JSON |
| Complete URL audio | Original URL in native JSON; no implicit download |
| `SpeechRequest` bridge | Explicit text, voice, format and sample rate to native fields; hex output |
| SSE / WebSocket / asynchronous synthesis | Unsupported by this entry; `stream=true` fails before I/O |

```csharp
using Asgard.Loki.Audio.MiniMax;

var adapter = new MiniMaxSpeechAdapter(httpFactory, new MiniMaxSpeechOptions
{
    Model = nativeSpeechModel,
    ApiKey = speechApiKey,
    Endpoint = new Uri("https://api.minimax.cn/v1/t2a_v2")
});
// Register the adapter in AudioOptions.SpeechRoutes with a public route.
```

For native URL output, keep the original JSON fields in `NativeSpeechRequest.Parameters`:

```csharp
using System.Text.Json;
using Asgard.Loki.Audio;

using var body = JsonDocument.Parse("""{"text":"Hello","output_format":"url"}""");
var request = new NativeSpeechRequest
{
    Model = "public-speech-route",
    Parameters = body.RootElement.EnumerateObject()
        .ToDictionary(x => x.Name, x => x.Value.Clone())
};
await foreach (var chunk in adapter.StreamNativeAsync(request, cancellationToken))
{
    var originalUrl = chunk.Data.GetProperty("data").GetProperty("audio").GetString();
    // The application owns any later use or download of this URL.
}
```

The default endpoint is the Chinese native interface shown above. International or regional targets must be explicitly configured, for example `https://api.minimax.io/v1/t2a_v2`. Endpoint overrides are complete HTTP(S) addresses without credentials, query or fragment. Credentials and actual model are fixed in this adapter; the public `NativeSpeechRequest.Model` is a route name and is never sent upstream. No model/voice catalog, account discovery, retry, fallback or URL fetching is performed. The named HTTP client defaults to `Asgard.Loki.Audio.MiniMax` and its factory remains borrowed.

Native parameters include required `text` and original optional `voice_setting`, `audio_setting`, `pronunciation_dict`, `timbre_weights`, `language_boost`, `voice_modify`, subtitles, watermark and unknown fields. A native `output_format` of `hex` or `url` selects the expected result carrier; omission retains the upstream hex default without injecting the field. Optional settings stay omitted. Native numbers, zero/false/null and string spelling remain intact. Root `model` overrides, controlled-field aliases, malformed Unicode and duplicate fields fail before I/O. `stream` must be absent or false; it is never silently changed.

The public bridge maps `Text` to `text`, `Voice` to `voice_setting.voice_id`, and `Format`/`SampleRate` to `audio_setting.format`/`sample_rate`. Keep other native root and voice settings in `Parameters`, and extra audio settings in `AudioParameters`. Mapped-field conflicts fail before I/O. `output_format=url` requires the native entry; the byte-oriented public bridge does not pretend a remote URL is downloaded audio.

`Speech.GenerateNativeAsync` aggregates hex bytes and retains complete frames. For URL mode its audio bytes are empty and the original URL remains at `Frames[0].data.audio`; applications explicitly consume/download it according to their own lifetime and transport policy. Native `trace_id`, subtitles, usage and unknown result fields are unchanged; no LLM token estimates are invented. Native success requires `base_resp.status_code=0`, `data.status=2` and a nonempty valid output of the selected carrier. The completed chunk is published only after the full body, length checks, resource disposal and final cancellation succeed. Native/HTTP failures retain original bytes in `MiniMaxSpeechException`.

Local defaults bound request JSON to 1 MiB, response JSON to 32 MiB and decoded hex audio to 16 MiB; upstream model limits can be lower. Resources created per call are disposed before success, with no application-owned stream or factory disposal. This is a library, with no task polling or shared LLM/Audio coordinator.

Official contracts: [Chinese](https://platform.minimax.cn/docs/api-reference/speech-t2a-http), [international](https://platform.minimax.io/docs/api-reference/speech-t2a-http). Tests use independent native request/result fixtures and offline replacement HTTP transports. No live MiniMax model/account/voice acceptance or audio quality is verified. See [architecture](../../docs/architecture.md) and [testing](../../docs/testing.md).
