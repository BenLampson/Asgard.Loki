# Asgard.Loki.Audio.Azure

Native deployment-scoped Azure audio calls, independent of LLM, Image and other suppliers. Depends only on Audio, Infrastructure and the standard HTTP factory package.

| Entry | Native operation | Supported result |
|---|---|---|
| `AzureSpeechAdapter` | JSON `audio/speech` | Ordered binary audio, native/convenience requests, full HTTP metadata |
| `AzureAudioFileAdapter.TranscribeAsync` | multipart `audio/transcriptions` | Complete original JSON/text/subtitle bytes |
| `AzureAudioFileAdapter.TranslateAsync` | multipart `audio/translations` | Native English translation; complete JSON/text/subtitle bytes |
| Authentication | Explicit `api-key` | No LLM key borrowing, credential discovery or implicit Bearer fallback |
| Other profiles | Unsupported | No Azure v1 auto-switch, Entra token acquisition, native SSE, voice/file management or polling |

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.Azure;

var speech = new AzureSpeechAdapter(httpFactory, new AzureAudioOptions
{
    Id = "azure-speech",
    Endpoint = new Uri(resourceEndpoint),
    Deployment = speechDeployment,
    ApiVersion = selectedSpeechApiVersion,
    ApiKey = speechKey,
    BodyModel = optionalNativeBodyModel
});
var files = new AzureAudioFileAdapter(httpFactory, new AzureAudioOptions
{
    Id = "azure-files", Endpoint = new Uri(resourceEndpoint),
    Deployment = recognitionDeployment, ApiVersion = selectedRecognitionApiVersion, ApiKey = recognitionKey
});
await using var loki = new LokiClientBuilder().AddAudio(new AudioOptions
{
    SpeechRoutes = [new() { Model = "speak", Targets = [speech] }],
    TranscriptionRoutes = [new() { Model = "recognize", Targets = [files] }],
    TranslationRoutes = [new() { Model = "translate", Targets = [files] }]
}).Build();
```

The caller explicitly configures the resource root, deployment and API version. There is no default Azure resource or version and no deployment-name normalization. The URI is `{root}/openai/deployments/{deployment}/audio/{operation}?api-version={version}`. A root can include an explicit proxy prefix, without credentials/query/fragment. Versions are encoded as a single query value. Deployments remain distinct from public routes and underlying model names; no deployment-to-model lookup occurs. `AudioFileResult.Metadata.UpstreamModel` records the actual native deployment selector.

`BodyModel` is an optional fixed native JSON field/multipart part; it is omitted unless configured and never copied from the public route or guessed from deployment. Core `model`/`file` overrides and controlled aliases fail before I/O. The named HTTP client defaults to `Asgard.Loki.Audio.Azure`. Factories are borrowed; factory-created clients/content/responses/streams are call-owned. The application must disable automatic inference retry/redirect handler policies; changed final methods/URIs cannot publish success.

Speech supports `GenerateNativeAsync`/`StreamNativeAsync` with original `input`, string `voice`, optional native format/speed and unknown JSON. Omitted/null settings and exact native numbers remain unchanged. The convenience entry maps only Text/Voice/Format plus explicit root Parameters; independent SampleRate/AudioParameters mappings and native SSE are unsupported before I/O. No voice catalog, input truncation, format coercion or estimated tokens/duration is added. Binary chunk metadata is an explicitly named `azure.audio.http` SDK transport record, including original multi-value HTTP headers, public route, deployment, API version and received byte count; it is not a native usage event.

File operations use existing `AudioFileRequest`/`AudioUpload`. Literal native parts retain order, duplicates, empty values and textual JSON/number spellings. Input streams remain caller-owned, readable once from their current position without length/position inspection, buffering, rewind or close. File names are sent as metadata and never opened by the SDK. Translation is performed by the native Azure operation; the SDK does not translate returned text itself. Explicit `stream=true` is rejected, with no downgrade to complete results.

Successful calls require native HTTP 200 and complete transport/body validation. Binary speech must contain audio, match any explicit known format and finish its declared length. File JSON must contain native text and no error; text/JSON formats cannot be silently swapped. Unknown JSON, segments, usage, words, nested errors and original SRT/VTT line endings remain intact. Final speech completion and file results wait for outer resource disposal and final cancellation. HTTP/native failures retain raw bytes/status/content type in `AzureAudioException`, including non-UTF-8 errors or HTTP 200 error envelopes. No automatic retry, fallback or target switch occurs.

Defaults: JSON/non-audio request parameters 1 MiB, complete file/error response 16 MiB, speech audio 64 MiB, upload 32 MiB, speech read buffer 8192 bytes. These are explicit local policies; upstream models/versions can impose lower limits. Upload overflow can consume one input block and leave partial transmitted data without replay. Model/account/deployment/voice permission and version availability remain caller/upstream decisions.

Protocol references: [TTS JSON quickstart](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/text-to-speech-quickstart?view=foundry-classic), [deployment preview audio reference](https://learn.microsoft.com/en-us/azure/foundry/openai/reference-preview), [GA transcription/translation](https://learn.microsoft.com/en-us/azure/foundry/openai/reference). Offline xUnit v3 tests use independent JSON/multipart/audio fixtures and replacement HTTP transports. No live Azure account/model/deployment acceptance, audio/recognition/translation quality or current regional entitlement is verified. See [architecture](../../docs/architecture.md) and [testing](../../docs/testing.md).
