# Asgard.Loki.Audio

Independent audio capability for Asgard.Loki.

Provides `AddAudio()`, `.Audio.Speech.GenerateAsync()` and `.StreamAsync()`. Public speech routes select explicitly configured adapter targets. Audio aggregation preserves byte order, subtitles, usage and unknown provider metadata, and requires an explicit completion marker.

Optional `.Audio.Speech.GenerateNativeAsync()` / `.StreamNativeAsync()` accept a `NativeSpeechRequest`: the public route `Model` plus supplier-native JSON `Parameters`. Adapters opt in through `INativeSpeechAdapter`; existing `ISpeechAdapter` implementations are unchanged. Existing custom `ISpeechClient` implementations remain source compatible through default unsupported native methods. The native aggregate `NativeSpeechResult` contains ordered bytes and frames without inventing a vendor-default audio format.

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;
using System.Text.Json;

INativeSpeechAdapter target = applicationOwnedNativeSpeechAdapter;
await using var loki = new LokiClientBuilder().AddAudio(new()
{
    SpeechRoutes = [new() { Model = "native-voice", Targets = [target] }]
}).Build();
var result = await loki.Audio.Speech.GenerateNativeAsync(new NativeSpeechRequest
{
    Model = "native-voice",
    Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement("Hello"),
        ["voice"] = JsonSerializer.SerializeToElement(new { id = "caller_selected_voice" }),
        ["response_format"] = JsonSerializer.SerializeToElement("pcm")
    }
}, cancellationToken);
// These field names belong to the application-owned adapter's native protocol.
```

The core snapshots native JSON, rejects a root `model` override/Undefined values and does not impose supplier `input`/voice/settings types. Every target in a native route must implement the optional contract. Mixed or unsupported routes fail before selection; the SDK does not filter targets, silently fall back to the convenience request or retry another target. Adapters remain caller-owned after client disposal. A native adapter must validate its actual fixed target, request, response and completion semantics; the core contract alone implements no upstream protocol.

Both entry paths hold at most the completed chunk until source enumeration **and disposal** finish normally, then expose its original audio and metadata once. Ordinary incomplete chunks still stream immediately. A late/duplicate frame, missing completion, tail/disposal error or cancellation fails without first publishing a successful terminal chunk. Consumer early disposal releases the source and does not generate success. The supplier defines how transport/protocol completion is established; the core never treats an arbitrary EOF as a native success marker.

Register a provider separately: `Asgard.Loki.Audio.Volcengine` for the existing convenience path, or [Asgard.Loki.Audio.OpenAI](../Asgard.Loki.Audio.OpenAI/README.md) for native JSON and convenience binary/SSE speech calls. Binary transport records explicit SDK HTTP metadata; SSE envelopes retain native JSON/raw data and event identity without manufacturing events. This capability does not depend on LLM, images, the entry implementation or a provider. It does not reuse LLM Session or routing state.

## Native file transcription and translation contracts

These clients send audio files as inputs to transcription/translation requests. They do not implement independent provider file upload/list/retrieve/download/delete operations or fine-tuning jobs. References to “file operations” in Audio documentation mean these audio inference calls.

`.Audio.Transcriptions.CreateAsync(AudioFileRequest)` and `.Audio.Translations.CreateAsync(AudioFileRequest)` call explicitly configured application-owned adapters. Transcription optionally exposes `.StreamAsync` through `IStreamingAudioTranscriptionAdapter`; all targets in the route must implement it before a stream can be selected. The OpenAI package implements native multipart transcription/translation, native transcription SSE and speech synthesis. Its optional streaming interface preserves delta/segment/done events and validates segment identity/completion without rewriting native text.

`AudioOptions` can configure SpeechRoutes, TranscriptionRoutes and TranslationRoutes independently; at least one operation must have a route. Accessing an unconfigured operation fails. Existing speech initialization and interfaces continue to work. File-operation routes/targets are snapshotted at registration; each client, file operation and model route has its own selection state. There is no Session, shared LLM routing, filtering, retry, failover or fallback to another Audio operation.

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;

IAudioTranscriptionAdapter target = applicationOwnedTranscriptionAdapter;
await using var loki = new LokiClientBuilder().AddAudio(new AudioOptions
{
    TranscriptionRoutes = [new() { Model = "recording", Targets = [target] }]
}).Build();
// File access and lifetime belong to the application, never the SDK.
await using var input = File.OpenRead(audioPath);
var result = await loki.Audio.Transcriptions.CreateAsync(new AudioFileRequest
{
    Model = "recording",
    File = new() { Content = input, FileName = "recording.wav", ContentType = "audio/wav" },
    Fields = [new("response_format", "verbose_json"), new("timestamp_granularities[]", "word")]
}, cancellationToken);
// result.Body / ContentType preserve the complete native response; inspect native JSON as needed.
```

`AudioUpload.Content` is a caller-owned readable stream, validated at dispatch and excluded from JSON serialization. The core does not read its length/position, prebuffer, rewind, dispose or open a filename. Metadata is independently snapshotted while stream identity/current position remain unchanged. Keep the stream alive until enumeration/call ends, do not concurrently consume or mutate it, and provide a fresh upload (or explicitly reposition it yourself) for a new call. Adapters must preserve this borrowed ownership when disposing their HTTP/multipart resources.

`AudioMultipartField` keeps literal native text names/values, including repeated names, empty values and original ordering. Nested/array settings use the actual supplier's native part names/text; the core does not guess JSON-to-form expansion or defaults. Root model/file overrides, null fields/values and header control characters fail before target dispatch. Unknown parameter syntax, media types and response structures remain supplier responsibilities.

`AudioFileResult` copies complete native body bytes and preserves the actual content type (including charset) plus public/upstream/provider identities. JSON segments/words/speakers/usage and text/subtitle formats are not normalized into a universal transcript. The provider must validate its own native HTTP/business success, errors and result structure. `AudioTranscriptionEvent` clones complete JSON and retains SSE name/ID/retry; the core does not infer terminal semantics from event names. It holds the original completed event until source enumeration/disposal succeeds, rejects missing/duplicate/late events and does not aggregate native text deltas into a guessed final result. Early consumer disposal cleans up the source without taking the upload/adapter's ownership.

## Verification and remaining operations

The 122 offline core cases include 84 file-operation contract cases and the existing 38 speech cases. File cases use application-owned in-memory adapters, complete authored JSON/WEBVTT body samples and nonseekable observed uploads. They check literal form snapshots, independent routing, raw results/events, cancellation, non-failover, optional streaming gates and success-after-source-end/disposal. They do not implement or verify actual multipart wire encoding, OpenAI transcription/translation, speech recognition quality or live models.

The speech subset covers native objects/unknown JSON, snapshots, target choice/non-failover, optional-interface compatibility, borrowed ownership and shared completion/error/cancellation/disposal semantics. Music generation and bidirectional speech remain unimplemented; Other native file suppliers, music generation and bidirectional operations remain future work.
