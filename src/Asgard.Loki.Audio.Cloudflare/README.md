# Asgard.Loki.Audio.Cloudflare

Explicit native Cloudflare Workers AI audio inference, independent of LLM and other capabilities.

| Explicit input profile | Recognition | Translation | Parameters |
|---|---|---|---|
| `RawBinary` | Original file bytes to `ai/run` | Unsupported: raw upload has no task field | Extra fields fail before I/O |
| `JsonBase64` | Native `task=transcribe` | Native `task=translate` | Original JSON values; incremental Base64 from borrowed upload |

The model must support the selected native schema. The official [Whisper input schema](https://developers.cloudflare.com/workers-ai/models/whisper/schema-input.json) documents raw bytes; [Whisper Turbo input](https://developers.cloudflare.com/workers-ai/models/whisper-large-v3-turbo/schema-input.json) documents JSON audio and explicit tasks. The adapter never guesses from model names, switches profiles or labels ordinary raw recognition as translation.

```csharp
using Asgard.Loki.Audio.Cloudflare;

var adapter = new CloudflareAudioFileAdapter(httpFactory, new CloudflareAudioFileOptions
{
    AccountId = accountId,
    Model = "@cf/openai/whisper-large-v3-turbo",
    ApiToken = workersAiToken,
    InputFormat = CloudflareAudioInputFormat.JsonBase64
});
// Register in AudioOptions.TranscriptionRoutes and/or TranslationRoutes.
```

For example, register the JSON target above and explicitly request native translation:

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;

await using var loki = new LokiClientBuilder().AddAudio(new AudioOptions
{
    TranslationRoutes = [new() { Model = "translate-recording", Targets = [adapter] }]
}).Build();
await using var input = File.OpenRead(audioPath);
var result = await loki.Audio.Translations.CreateAsync(new AudioFileRequest
{
    Model = "translate-recording",
    File = new() { Content = input, FileName = "recording.wav", ContentType = "audio/wav" },
    Fields = [new("language", "\"zh\""), new("vad_filter", "false")]
}, cancellationToken);
// result.Body retains the complete native JSON envelope.
```

The default API root is `https://api.cloudflare.com/client/v4/`; account and model form `accounts/{account}/ai/run/{model}`. Configure alternate roots explicitly. Root, account, actual model, profile and credential belong to this fixed target. `AudioFileRequest.Model` is only a public route and stays separate in result metadata. The named HTTP client defaults to `Asgard.Loki.Audio.Cloudflare`; its factory is borrowed.

The application owns HTTP handler policy and must disable automatic retry/redirect policies for these single-use inference calls. A changed final request URI/method cannot publish a result; serialization replay is rejected.

Both profiles use existing `AudioFileRequest`/`AudioUpload`. The stream is consumed once from its current position, without length/position inspection, rewind, buffering or close. File names are validation metadata, never opened locally or sent as invented native fields. Raw mode uses the original upload `ContentType` or `application/octet-stream` if omitted. JSON mode uses HTTP `application/json` and Base64 audio, without inventing MIME/file-name fields.

In JSON mode each `Fields` value is **native JSON text**, not an implicitly quoted string: `new AudioMultipartField("language", "\"zh\"")`, `new("vad_filter", "false")`, or `new("beam_size", "5")`. Objects/arrays/null/exact numbers keep their types. The operation supplies `task`; a supplied task must match it. Duplicate fields, controlled aliases, model/file/audio overrides and invalid JSON/Unicode fail before I/O. No model catalog or parameter defaults are inferred. Raw mode rejects all fields rather than discarding them. No multipart or response streaming is implemented.

`AudioFileResult` retains complete original UTF-8 JSON bytes and actual content type: result text, words, segments, transcription metadata, VTT, diagnostics, unknown fields and exact native numbers. Empty text is valid. Success requires native `success=true`, an empty errors array and a result text string. HTTP/native failures retain raw bytes in `AudioFileUpstreamException`; business failures may carry HTTP 200. No LLM usage is synthesized.

Success waits for bounded body/declared-length checks, call-owned resource disposal and final cancellation. Upload limits count raw audio even with Base64. Defaults: upload 16 MiB, non-audio JSON parameters 64 KiB, response JSON 16 MiB; upstream limits can be lower. Overflow can consume one read block and leave partial output; it never rewinds/retries. No automatic retry, failover, task polling, account discovery, Worker deployment, audio splitting or external URL fetching is included.

Offline xUnit v3 tests use independent WAV/JSON fixtures and replacement HTTP handlers. No live account/model acceptance or recognition/translation quality is verified. Official [output schema](https://developers.cloudflare.com/workers-ai/models/whisper-large-v3-turbo/schema-output.json) and [REST envelope](https://developers.cloudflare.com/workers-ai/get-started/rest-api/); see [architecture](../../docs/architecture.md) and [testing](../../docs/testing.md).
