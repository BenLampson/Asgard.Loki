# Asgard.Loki.Audio.OpenAI

Native OpenAI Speech binary/SSE and file transcription/translation transport for the independent Audio capability. Depends only on `Asgard.Loki.Audio` and `Microsoft.Extensions.Http`; no LLM, image, web server or background service.

## Support matrix

| Surface | Support |
| --- | --- |
| `POST /audio/speech` JSON → binary audio | Implemented; buffered aggregation or incremental byte reads |
| Native input, string/custom-object voice, instructions, speed, response_format, unknown JSON | Preserved without a model/voice catalog; only the public route model is replaced by the fixed upstream model |
| MP3, Opus, AAC, FLAC, WAV, PCM | Opaque byte delivery; generic octet-stream and other audio MIME types also accepted |
| Native omission, explicit null, false and nested fields | Retained; no default speed, format or stream_format injected |
| Convenience `SpeechRequest` | Text/Voice/Format map to input/voice/response_format; Parameters retained; conflicting root fields rejected |
| Explicit `stream_format: "sse"` | Native speech.audio.delta Base64 audio and speech.audio.done usage; incremental consumption or aggregation |
| Unknown SSE events/fields | Complete JSON and raw data text retained; unknown events emit metadata, never guessed audio/completion |
| File transcription | Native multipart; JSON/verbose/diarized objects and text/subtitle bytes retained |
| File translation | Native multipart; native JSON/verbose/text/subtitle results retained |
| File transcription SSE | Native delta/segment/done; complete JSON, logprobs, languages, usage and SSE identity |
| Voice creation/consent uploads, playback, decoding, resampling | Outside this implementation |
| Real-provider verification | Not performed; offline HTTP tests only |

Protocol references: [Speech API](https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create) and [text-to-speech guide](https://developers.openai.com/api/docs/guides/text-to-speech). Model/account capabilities and range/length limits remain upstream decisions; the SDK does not infer them from model names or modify the request to bypass errors.

The 2026-10-19 source review confirmed native SSE in the current create reference/OpenAPI. The indexed [legacy official Audio event reference](https://platform.openai.com/docs/api-reference/audio/json-object?lang=csharp) describes delta.audio and done.usage; its current rendered redirect does not expose those event definitions. This implementation targets those two documented event shapes and preserves extensions; it is not a live-account confirmation of current model behavior or every future event variant.

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
        ["stream_format"] = JsonSerializer.SerializeToElement("sse"),
        ["instructions"] = JsonSerializer.SerializeToElement("Speak softly.")
    }
}, cancellationToken);
// result.Audio contains decoded audio bytes; SSE Frames retain each native event under "data".
```

Use `StreamNativeAsync` to consume `SpeechChunk.Audio` incrementally, or `GenerateAsync` / `StreamAsync` with the existing convenience request. Select SSE explicitly through native Parameters (also supported by the convenience request); omission/null keeps binary mode. The SDK never changes a model or drops fields to make a model accept SSE. A native request has no universal Format field on its result. Convenience SampleRate and nonempty AudioParameters have no verified mapping here and fail explicitly; they are not silently discarded or renamed into another provider's dialect.

## Transport and limits

- Normal binary HTTP EOF, a nonempty body and observed Content-Length consistency establish transport completion. No semantic audio/codec integrity or SSE done event is inferred. Without a declared length, an otherwise normal early EOF cannot be distinguished from a complete audio body by this layer. HTTP framing errors remain the HTTP handler's responsibility.
- Data chunks carry copied bytes, so subsequent reads cannot overwrite earlier chunks. ReadBufferSize (1 byte–1 MiB) controls a local read buffer, not media frames or a total-size limit. Aggregation buffers the complete audio; stream large outputs instead.
- The empty-audio completed chunk appears only after reading and disposing stream/response/client successfully. Read errors, length mismatch, cancellation or disposal failure prevent it. Already emitted partial bytes cannot be recalled; aggregate generation fails without returning a successful result. Consumer early disposal closes transport without declaring success.
- Each chunk's Data (and aggregate Frames) is explicitly marked `transport: "http_audio"`: public_model, target_model, HTTP status, multi-value response/content headers, cumulative bytes_received and completed. These records are SDK metadata, not upstream JSON, native usage, subtitles or SSE events. HTTP header names follow .NET normalization; values are retained. Request authentication is not included.
- SSE Data/Frames use `transport: "sse"`, the same target/HTTP metadata, original `data` JSON, exact `raw_data` text and `event_name`, `event_id`, `retry_ms`. Native fields are never inserted into or removed from the event JSON. Delta.audio is canonical standard Base64 decoded once into the corresponding chunk; original Base64 remains in Data. Unknown events carry empty Audio. Aggregation retains envelopes in original order, including unknown events. Both aggregation and streaming retain SSE identity. Base64 decoding does not decode a media codec.
- SSE requires one native speech.audio.done, then normal event/UTF-8/length completion and transport disposal. Native usage requires its three nonnegative JSON integer token counts, without an Int64 cap or SDK recomputation; unknown usage fields remain unchanged. A valid done may confirm zero audio bytes. Comments after done are allowed; duplicate or later data events, partial event tails, invalid UTF-8/JSON, duplicate JSON members and generic [DONE] sentinels fail. The SDK neither promotes EOF/[DONE] to native success nor reconnects. Error envelopes/typed/error-named events retain raw text and actual HTTP status in SpeechUpstreamException, including errors arriving after a held done.
- SSE framing uses the shared Infrastructure reader with strict UTF-8 (an initial UTF-8 BOM is accepted; other encoding detection/replacement is disabled). HTTP Content-Length counts original SSE bytes, never decoded audio length. Event name/type conflicts fail; default message events use the native JSON type. SSE framing identities follow standard last-value/persistence rules, with no automatic retry based on retry_ms. ReadBufferSize applies to binary reads; the SSE text parser has its own buffering and no configured event-size cap.
- Missing/non-audio content types and known MIME/format contradictions fail; an explicit SSE request cannot silently fall back to binary, or vice versa. Generic octet-stream and unrecognized audio types carry no codec guarantee. HTTP failures and JSON/text error bodies retain the actual status and full text in SpeechUpstreamException; a success status carrying JSON/text is also rejected. No audio sniffing, codec decoding, resampling or binary-to-text conversion is performed.
- API roots must be HTTP(S) absolute URIs without user-info, query or fragment. Organization, Project and named HTTP clients are optional target settings. No automatic retries, credential rotation, fallback, downloads or failover are configured by this adapter.

## Verification

378 xUnit v3 offline tests comprise 103 speech binary/request cases, 70 speech SSE cases, 99 nonstream file-operation cases and 106 transcription SSE cases. Speech tests use independent complete native/convenience/SSE request goldens, full SSE events and terminal envelope snapshots, opaque bytes (including zero/invalid UTF-8 values) and substitute HTTP handlers/fragmented streams. They cover all six binary formats, unknown/null fields, custom voice objects, snapshots, raw errors, headers, lengths, framing/encoding, audio-event association, native usage, partial/complete results, cancellation and early/error disposal. Byte fixtures are not valid media files and do not establish speech quality, model acceptance or container validity. Aggregation retains full raw events and audio in memory; use streaming and discard processed chunks for large outputs. No real key, network, Redis or GitHub Actions is used by these tests.

## File transcription and translation

`OpenAiAudioFileAdapter` implements `IAudioTranscriptionAdapter` and `IAudioTranslationAdapter` using `POST audio/transcriptions` / `audio/translations`. Configure its own `OpenAiAudioFileOptions` with a fixed native model, API root, credential, Id and optional organization/project/named client. Use different targets when the operations require different models or identities. The SDK does not choose models or credentials based on the operation. References: [file transcription/translation guide](https://developers.openai.com/api/docs/guides/speech-to-text), [transcription API](https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create), [translation API](https://developers.openai.com/api/reference/resources/audio/subresources/translations/methods/create).

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.OpenAI;

var fileTarget = new OpenAiAudioFileAdapter(httpClientFactory, new OpenAiAudioFileOptions
{
    Id = "file-one", Model = transcriptionModel, ApiKey = fileApiKey
});
await using var files = new LokiClientBuilder().AddAudio(new AudioOptions
{
    TranscriptionRoutes = [new() { Model = "recording", Targets = [fileTarget] }]
}).Build();
await using var input = File.OpenRead(audioPath); // Application owns file access/lifetime.
var transcript = await files.Audio.Transcriptions.CreateAsync(new AudioFileRequest
{
    Model = "recording", File = new() { Content = input, FileName = "recording.wav", ContentType = "audio/wav" },
    Fields = [new("response_format", "verbose_json"), new("timestamp_granularities[]", "word")]
}, cancellationToken);
// transcript.Body and ContentType are the complete native result, not a normalized transcript.
```

Register TranslationRoutes and call `.Audio.Translations.CreateAsync` for the translation operation with its caller-selected target. Native text names/values, repeated array names, nested bracket syntax, speaker-reference data URLs, empty fields and unknown fields are sent unchanged and in order. Only the public alias is replaced by the actual model part; model/file overrides and duplicate response_format/stream controls fail. No response_format, stream, temperature or language defaults are inserted. In the absence of response_format, validation follows the native JSON default without adding a part. No JSON-to-form expansion, media fetch, speaker-reference decoding, file extension inference or model-specific field deletion is performed.

Uploads are read once from the caller's current position, including nonseekable streams. No length query, rewind, input buffering or filename opening occurs. A private HttpContent leaves the source open when the request/client is disposed and rejects a second serialization, including a resend requested by an external handler. Native form text uses strict UTF-8; unpaired UTF-16 surrogates in models, field names/values or filenames fail before HTTP rather than being replaced. The caller owns upload lifetime and supplies fresh content for a new invocation. The SDK does not add retries/failover, upload splitting or the upstream's model-specific size/language/quality policy.

The adapter reads the complete nonstream response and returns only after stream/response/client disposal succeeds. Known JSON formats require an unambiguous UTF-8 object with string text; native error envelopes, incomplete JSON, duplicate success members, format/media contradictions, transport faults and length mismatches fail explicitly. Segments/words/speakers/logprobs/languages/usage and future fields are retained byte-for-byte, without semantic normalization, range repair, counter arithmetic or recognition/language-quality claims. Native text/subtitles are not decoded/re-encoded; actual content type/charset is retained. Empty text and JSON BOM bytes remain unchanged. HTTP and native business errors expose actual status, content type and full raw Body through capability-owned AudioFileUpstreamException, including binary/unknown-encoding error bodies.

The ordinary file-result methods require native stream=false or omission; they never turn SSE into a reconstructed file result. Unexpected SSE/non-result media on those methods fail before reading. JSON/text output is buffered in memory without a configured response-size cap. No headers/result views beyond the declared raw-body/content-type/target contract or semantic validation of complete-file timestamps/speakers are claimed. Use the separate transcription streaming operation described below for native events.

File tests compare five independently authored complete multipart goldens against actual serialized bytes (normalizing only the random boundary), plus complete JSON/diarization/subtitle bodies. They cover native settings and unknown fields, default omission, Unicode filenames/current position, direct snapshots, all declared format families, raw errors, upload/download/read/disposal/cancellation failures, no replay/failover and borrowed ownership. They do not contact OpenAI or certify a model/account, audio container or transcription/translation accuracy.

## Native file transcription SSE

`OpenAiAudioFileAdapter` implements `IStreamingAudioTranscriptionAdapter`. Call `.Audio.Transcriptions.StreamAsync(request, cancellationToken)` or the adapter's explicit StreamAsync method. It adds only the native `stream=true` part when omitted, preserves a supplied true part in place, and rejects a supplied false or duplicate/invalid stream controls before HTTP. It uses the same fixed transcription target, one-shot upload and borrowed ownership rules. Translation remains a separate nonstream operation. No model-name heuristic is used: if a model ignores streaming and returns a normal JSON result, the operation fails instead of changing models or manufacturing events.

```csharp
await using var streamInput = File.OpenRead(audioPath); // A fresh caller-owned upload for this call.
await foreach (var item in files.Audio.Transcriptions.StreamAsync(new AudioFileRequest
{
    Model = "recording", File = new() { Content = streamInput, FileName = "recording.wav", ContentType = "audio/wav" }
}, cancellationToken))
{
    ProcessNativeEvent(item); // Data retains native delta/segment/done and unknown events.
}
```

The [official transcription event reference](https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/streaming-events) defines transcript.text.delta, transcript.text.segment and transcript.text.done. Every AudioTranscriptionEvent retains original JSON/Data.GetRawText, separate public/upstream/provider identity and original SSE name/id/retry. Optional languages, logprobs, token bytes, usage, explicit nulls, future usage variants and unknown events/fields are preserved. Native final text/segment text are not rewritten from deltas; totals are not recalculated. No event is promoted to completion based on its SSE name, HTTP EOF or a generic [DONE] marker.

Delta segment_id references are tracked independently of SSE id. Completed segment ids must be unique, may arrive without earlier deltas, and may overlap in time with other segments. Interleaved pending ids are accepted. Reopened ids, delta references after segment completion, unresolved references at global done and invalid/nonfinite times fail. Known time/range and byte-integer comparisons use original decimal lexemes, avoiding floating-point rounding of adjacent values; original numbers remain unchanged. Known numeric fields must be finite in binary64 validation; no numeric view or re-encoding is returned. Optional native containers/known types are checked, while unknown fields and usage variants are not coerced into an invented schema.

MaxTrackedTranscriptionSegments defaults to 100,000 and must be positive. It bounds unique observed segment identities (pending and completed) per stream; the SDK fails at the limit rather than evicting ids, truncating events or retrying. It does not accumulate transcript text or persist cross-call state. SSE framing uses strict UTF-8 with an initial UTF-8 BOM allowed, original HTTP byte-length validation and no configured per-event byte cap. One valid native done is held until the source and all transport resources end/dispose normally; duplicate/late data, error events even after done, truncated tails, cancellation or disposal errors prevent successful completion. Partial events already delivered cannot be recalled.

SSE errors throw AudioFileUpstreamException with actual HTTP status, content type and original data payload bytes. Its optional TranscriptionEvent retains the raw error JSON, target and SSE identity (including name/id/retry); ordinary HTTP/nonstream errors leave that property null rather than fabricating an event. HTTP error responses retain their complete body. Unknown events before completion pass through as nonterminal native events. Native errors are not converted into ordinary text, speaker events or success.

106 additional offline cases include two complete MIME request goldens, full native event/terminal snapshots, interleaved id references, native final-text differences, optional metadata and exact numeric boundaries. Failure cases cover malformed/duplicate/UTF-8 JSON, bad shapes/usage/times/bytes, id reopening/orphans/limits, wrong transport, late errors, incomplete/duplicate/missing done, read/disposal/length/cancellation and early consumer stop. Live account/model acceptance, diarization quality and real-media transcription remain unverified.

## Transport dependency

This package references `Asgard.Loki.Audio`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.
