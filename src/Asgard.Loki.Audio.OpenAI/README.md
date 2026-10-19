# Asgard.Loki.Audio.OpenAI

Native OpenAI Speech binary transport for the independent Audio capability. Depends only on `Asgard.Loki.Audio` and `Microsoft.Extensions.Http`; no LLM, image, web server or background service.

## Support matrix

| Surface | Support |
| --- | --- |
| `POST /audio/speech` JSON → binary audio | Implemented; buffered aggregation or incremental byte reads |
| Native input, string/custom-object voice, instructions, speed, response_format, unknown JSON | Preserved without a model/voice catalog; only the public route model is replaced by the fixed upstream model |
| MP3, Opus, AAC, FLAC, WAV, PCM | Opaque byte delivery; generic octet-stream and other audio MIME types also accepted |
| Native omission, explicit null, false and nested fields | Retained; no default speed, format or stream_format injected |
| Convenience `SpeechRequest` | Text/Voice/Format map to input/voice/response_format; Parameters retained; conflicting root fields rejected |
| Speech SSE events, transcription, translation | Pending; explicit SSE requests/responses fail rather than being reinterpreted as audio |
| Voice creation/consent uploads, playback, decoding, resampling | Outside this implementation |
| Real-provider verification | Not performed; offline HTTP tests only |

Protocol references: [Speech API](https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create) and [text-to-speech guide](https://developers.openai.com/api/docs/guides/text-to-speech). Model/account capabilities and range/length limits remain upstream decisions; the SDK does not infer them from model names or modify the request to bypass errors.

## Initialization

The application supplies the HTTP factory, credentials and cancellation token. The factory is borrowed; each factory-created client and response stream is disposed per invocation.

```csharp
using System.Text.Json;
using Asgard.Loki;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.OpenAI;

var target = new OpenAiSpeechAdapter(httpClientFactory, new OpenAiSpeechOptions
{
    Id = "openai-voice-one", Model = "caller-selected-tts-model", ApiKey = apiKey,
    BaseAddress = new Uri("https://api.openai.com/v1/")
});
await using var loki = new LokiClientBuilder().AddAudio(new AudioOptions
{
    SpeechRoutes = [new() { Model = "speech", Targets = [target] }]
}).Build();
var result = await loki.Audio.Speech.GenerateNativeAsync(new NativeSpeechRequest
{
    Model = "speech", Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement("Hello"),
        ["voice"] = JsonSerializer.SerializeToElement(new { id = customVoiceId }),
        ["response_format"] = JsonSerializer.SerializeToElement("pcm"),
        ["instructions"] = JsonSerializer.SerializeToElement("Speak softly.")
    }
}, cancellationToken);
// result.Audio contains unchanged bytes; result.Frames are SDK HTTP transport records.
```

Use `StreamNativeAsync` to consume `SpeechChunk.Audio` incrementally, or `GenerateAsync` / `StreamAsync` with the existing convenience request. A native request has no universal Format field on its result. Convenience SampleRate and nonempty AudioParameters have no verified mapping here and fail explicitly; they are not silently discarded or renamed into another provider's dialect.

## Transport and limits

- Normal binary HTTP EOF, a nonempty body and observed Content-Length consistency establish transport completion. No semantic audio/codec integrity or SSE done event is inferred. Without a declared length, an otherwise normal early EOF cannot be distinguished from a complete audio body by this layer. HTTP framing errors remain the HTTP handler's responsibility.
- Data chunks carry copied bytes, so subsequent reads cannot overwrite earlier chunks. ReadBufferSize (1 byte–1 MiB) controls a local read buffer, not media frames or a total-size limit. Aggregation buffers the complete audio; stream large outputs instead.
- The empty-audio completed chunk appears only after reading and disposing stream/response/client successfully. Read errors, length mismatch, cancellation or disposal failure prevent it. Already emitted partial bytes cannot be recalled; aggregate generation fails without returning a successful result. Consumer early disposal closes transport without declaring success.
- Each chunk's Data (and aggregate Frames) is explicitly marked `transport: "http_audio"`: public_model, target_model, HTTP status, multi-value response/content headers, cumulative bytes_received and completed. These records are SDK metadata, not upstream JSON, native usage, subtitles or SSE events. HTTP header names follow .NET normalization; values are retained. Request authentication is not included.
- Missing/non-audio content types and known MIME/format contradictions fail. Generic octet-stream and unrecognized audio types carry no codec guarantee. HTTP failures and JSON/text error bodies retain the actual status and full text in SpeechUpstreamException; a success status carrying JSON/text is also rejected. No audio sniffing, decoding or text/Base64 conversion is performed.
- API roots must be HTTP(S) absolute URIs without user-info, query or fragment. Organization, Project and named HTTP clients are optional target settings. No automatic retries, credential rotation, fallback, downloads or failover are configured by this adapter.

## Verification

103 xUnit v3 offline tests use independent complete native/convenience request goldens, a terminal metadata golden, opaque bytes (including zero/invalid UTF-8 values) and substitute HTTP handlers/fragmented streams. They cover all six formats, unknown/null fields, custom voice objects, snapshots, raw error bodies, headers, lengths, partial/complete results, cancellation and early/error disposal. Byte fixtures are not valid media files and do not establish speech quality, model acceptance or container validity. No real key, network, Redis or GitHub Actions is used by these tests.
