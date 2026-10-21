# Asgard.Loki.Audio.Volcengine

Independent Volcengine speech adapters for Asgard.Loki. No LLM session, voice mapping, background service, automatic retry or protocol fallback is involved.

## Support matrix

| Entry | Native contract | Authentication | Input / output |
|---|---|---|---|
| `VolcengineSpeechAdapter` | V3 `POST /api/v3/tts/unidirectional` | Exclusive API Key or app/access-key headers | Public `SpeechRequest`; ordered JSON/audio chunks |
| `VolcengineV1SpeechAdapter`, `Transport = Http` | V1 `POST /api/v1/tts`, `operation=query` | `Authorization: Bearer;{AccessToken}` | Native JSON or public-field bridge; complete JSON/Base64 audio |
| `VolcengineV1SpeechAdapter`, `Transport = WebSocket` | V1 `/api/v1/tts/ws_binary`, `operation=submit` | Same V1 header | One request per owned socket; binary audio and frontend metadata |

V1 is documented by Volcengine as **not recommended**. Select it deliberately when the application requires that native contract; V3 remains a separate entry. Official contracts: [V1 WebSocket](https://docs.volcengine.com/docs/DoubaoVoice/Websocketinterface?lang=zh), [request/response parameters](https://docs.volcengine.com/docs/DoubaoVoice/Parameterbasicdescription?lang=zh), [V1 HTTP](https://docs.volcengine.com/docs/DoubaoVoice/HTTPone-timecompositenon-streaminginterface-V1notrecommended?lang=zh).

## Initialization

For V3, create `VolcengineSpeechAdapter(IHttpClientFactory, VolcengineSpeechOptions)` and register it in `AudioOptions.SpeechRoutes`. Set `ResourceId` and either `ApiKey` or the exclusive `AppId`/`AccessKey` pair. The default named client is `Asgard.Loki.Audio.Volcengine`.

For V1, select the transport explicitly. HTTP requires a borrowed `IHttpClientFactory`; WebSocket uses `ClientWebSocket` by default or a borrowed `IVolcengineV1SpeechWebSocketFactory`. Each returned connection belongs to its single call.

```csharp
using System.Text.Json;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.Volcengine;

var adapter = new VolcengineV1SpeechAdapter(new VolcengineV1SpeechOptions
{
    AppId = appId,
    Cluster = cluster,
    AccessToken = accessToken,
    Transport = VolcengineV1SpeechTransport.WebSocket
});
using var body = JsonDocument.Parse("""
{
  "app": { "token": "body-marker" },
  "user": { "uid": "application-user" },
  "audio": { "voice_type": "your-authorized-native-voice", "encoding": "pcm" },
  "request": { "reqid": "unique-per-call-id", "text": "Hello", "text_type": "plain" }
}
""");
var request = new NativeSpeechRequest
{
    Model = "public-speech-route",
    Parameters = body.RootElement.EnumerateObject()
        .ToDictionary(x => x.Name, x => x.Value.Clone())
};
await foreach (var chunk in adapter.StreamNativeAsync(request, cancellationToken))
{
    // Audio bytes are ordered; empty metadata records are retained.
    // IsCompleted requires native and transport completion.
}
```

Register the adapter in a `SpeechRoute` with the matching public route to use `Speech.GenerateNativeAsync` for aggregation. The public route is never injected as the native model. Native `request.model`, voice and extension parameters retain original values; omitted encoding/rate/model stay omitted. String/object `extra_param`, exact numbers and unknown JSON fields are preserved.

V1 requires caller-provided `app.token` (a nonempty native body marker), `user.uid`, `audio.voice_type`, `request.reqid` and text. The body marker is independent of the header access token. Configured app ID/cluster and selected operation are injected only when omitted; conflicts and case aliases fail before I/O. Callers own request-ID uniqueness and native model/voice permissions.

The `SpeechRequest` bridge maps `Text`, `Voice`, `Format` and `SampleRate` to native request/audio fields. Supply `app`, `user`, and `request` (including `reqid` and native settings) through `Parameters`, and additional audio fields through `AudioParameters`. Conflicting mapped fields fail; no identity, body token or request ID is invented.

## Limits and completion

- V1 WebSocket supports uncompressed/gzip JSON requests and responses, no-sequence audio/ACKs, positive audio sequences and both documented negative-final flags. `wav` streaming fails before connection; HTTP `wav` is retained. Other native acceptance remains upstream-owned.
- Binary `SpeechChunk.Data` is a named `volcengine.v1.websocket` SDK transport record with original frame/payload Base64, header fields, sequence and unchanged frontend JSON where declared. Non-JSON frontend bytes remain raw. HTTP retains original JSON, including frontend JSON **strings**. No usage or subtitles are synthesized from text/audio length.
- Native negative-final audio is buffered while the SDK sends normal close and waits for normal peer close. Bounded trailing metadata remains ordered; extra audio, late errors, premature/abnormal close, timeout, disposal failure or cancellation cannot publish completion. A call must contain audio bytes. Early consumer disposal releases the socket without success.
- Defaults: request JSON 1 MiB, response message/HTTP body 4 MiB, decoded payload/audio 8 MiB, WebSocket wire and decoded totals independently 64 MiB, 4096 messages and 4096 receive fragments per message, close timeout 5 seconds. Options expose these local bounds; upstream limits can be lower. Request gzip is WebSocket-only. Unknown binary versions/events/compression fail explicitly.
- `VolcengineV1SpeechException` retains the full unsigned 32-bit binary error code, original response and decoded error bytes, plus HTTP status/content type where applicable. Raw errors are supplier data and can be sensitive.

Ark LLM credentials do not establish speech authorization. No ASR, music generation, V3 bidirectional WebSocket or replay is provided by these entries. Factories are borrowed; call-created clients, content, streams and sockets are released before success.

## Verification

Offline xUnit v3 tests use independent binary/JSON fixtures and replacement HTTP/WebSocket transports, covering field retention, association, compression, errors, bounds, cancellation and finalization. No V1 or V3 real-provider speech validation has been performed; tests do not prove account/model acceptance or audio quality. See [architecture](../../docs/architecture.md) and [testing](../../docs/testing.md).
