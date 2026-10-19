# Asgard.Loki.Audio

Independent speech capability for Asgard.Loki.

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

Register a provider separately, currently `Asgard.Loki.Audio.Volcengine` for the existing convenience path. No bundled provider implements the optional native JSON path at this core checkpoint; OpenAI speech transport remains the next supplier implementation. This package does not depend on LLM, images, the entry implementation or a provider. It does not reuse LLM Session or routing state.

Speech recognition, music generation and bidirectional speech are not implemented. See the root README for initialization.

Thirty-eight offline core cases (35 new native/terminal regressions and three existing cases) use only application-owned in-memory adapters. They cover native objects/unknown JSON, snapshots, target choice/non-failover, optional-interface compatibility, borrowed ownership and shared completion/error/cancellation/disposal semantics. They do not call or certify an OpenAI/Volcengine model or a native binary/SSE protocol.
