# Asgard.Loki.LLM.OpenAI

Authentication remains API-Key-only. `LlmCredential.External` is an explicit core identity for providers that implement their own token/signing source; this OpenAI adapter does not opt into it. Direct Chat/Responses JSON/SSE, native Moderations, compaction and WebSocket operations reject both anonymous and external identities before dispatch, rather than using an empty Key.

OpenAI LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Native Moderations | Complete native JSON; text/batch/multimodal input groups | No |
| Responses creation / continuation | Yes | SSE |
| Native Responses compaction | Yes, explicit target feature | No |
| Native Responses WebSocket | Explicit duplex connection | Native JSON events; named lanes |
| Native Realtime WebSocket | Explicit separate protocol/connection | Original JSON events; no response aggregation |

Create `OpenAiLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Configure targets with a `/v1/` base address, actual model and credential. The default provider ID is `openai`; the named HTTP client is `Asgard.Loki.LLM.OpenAI`.

Preserves extension parameters, tools, reasoning items, cache controls, original response IDs, usage, error events and unknown JSON. Session routing uses the shared LLM module. Configure project/organization defaults through the named HTTP client if needed. Disable POST retries and hedging.

## Native Realtime WebSocket

Declare `Protocols = LlmProtocol.Realtime` on the complete target. This protocol is independent of Responses and needs no Responses feature flag. The provider appends `/realtime?model={escaped actual model}` to the configured HTTP(S) root and maps HTTP(S) to WS(S), with an isolated Bearer handshake. The model is configured by the caller; there is no default model or automatic upgrade. The [official guide](https://developers.openai.com/api/docs/guides/realtime-websocket) describes this JSON protocol; GPT-Live, Responses WebSocket, WebRTC and SIP are separate interfaces.

```csharp
// Register this provider and a complete Realtime target in the existing LlmOptions.
var provider = new OpenAiLlmProvider(httpClientFactory,
    new OpenAiRealtimeOptions { MaximumMessageBytes = 16 * 1024 * 1024 });

await using var connection = await loki.LLM.Realtime.OpenAsync(
    "configured-public-model", new LlmCallOptions { SessionId = "realtime-conversation-1" }, cancellationToken);
await connection.SendAsync(new JsonObject
{
    ["type"] = "session.update",
    ["session"] = new JsonObject { ["type"] = "realtime", ["instructions"] = "Be concise." }
}, cancellationToken);
await foreach (var item in connection.ReadEventsAsync(cancellationToken))
{
    // Inspect original item.Data, including native error and response.done status.
    // The application drives input, tool results, response.create/cancel and closure.
}
```

`IOpenAiRealtimeWebSocketFactory` replaces the handshake for offline tests or application-owned transport configuration. It is borrowed; the returned open socket is owned/disposed once by the connection. The combined constructor accepts independent HTTP, Responses WebSocket and Realtime policies/factories. Default Realtime configuration preserves existing HTTP/Responses binding bytes; explicit changed limits/factory identity participate in complete-target binding. Server/model/credential/provider identity stays fixed. Known model-selection fields may repeat the public or actual model, but cannot change it; business `model` fields inside tool data are untouched.

Send snapshots precede waits. One reader and serialized writes permit duplex application code; no hidden receive loop or tool/audio state is added. The byte limit applies to complete incoming/outgoing JSON, including fragmented UTF-8. Original fields, audio base64, tool/item/call IDs, `event_id`, usage, unknown events and `response.done` statuses are returned. Metadata describes the bound target; native IDs remain in `Data` and are not registered as HTTP Responses continuation IDs.

`error` does not automatically end the connection. A `response.done` event may report completed, cancelled, incomplete or failed; it is not connection completion. Normal socket closure ends enumeration **without establishing model success** or synthesizing a terminal event; application code must inspect its expected events. Malformed envelopes, binary messages, limits, abnormal close and IO faults fail explicitly. Cancellation during IO stops uncertain transport without reconnect/replay. `CloseAsync` closes transport only; `DisposeAsync` releases it. Reopening a logical Session creates a new socket and does not restore remote history. No generation, playback, tool execution or billing loop is performed.

Independent `Fixtures/realtime.json` events cover native duplex behavior. Public SDK tests cover duplex fidelity, authentication/target affinity, original failure statuses, unknown fields, snapshots, limits, framing, cancellation and disposal. This is offline protocol verification; real OpenAI model/audio behavior is **not verified**. Ephemeral session provisioning, browser/WebRTC/SIP and cross-protocol Realtime conversion are outside this operation.

## Explicit compaction

The provider implements optional `IResponsesCompactionAdapter`. Set this on a target before using `loki.LLM.Responses.CompactAsync`:

```csharp
var target = new LlmTarget
{
    Id = "openai-compact", ProviderId = "openai",
    BaseAddress = new Uri("https://api.openai.com/v1/"),
    UpstreamModel = actualModel, CredentialId = "openai-key",
    Protocols = LlmProtocol.Responses,
    Features = LlmTargetFeatures.ResponsesCompaction
};
```

Register the target in an `LlmRoute` with the provider, caller-supplied credential, HTTP factory and existing Session dependencies. The [root example](../../README.md#native-responses-compaction) shows explicit compact/next-create calls. `ResponseCompactionRequest` preserves text/item-array input, optional owned `PreviousResponseId`, and instructions/cache/future parameters. Only the logical-to-upstream model mapping changes; there is no injected `stream`, automatic history retrieval or resubmission.

`ResponseCompaction.Data` contains the complete native result, including usage and unknown fields. `Output` exposes the full returned array, including retained messages, tool IDs and opaque encrypted compaction items. Do not extract only the encrypted item or discard retained items. An ordinary response or missing output array is rejected as an invalid compaction result. A native root error is an `LlmUpstreamException` retaining the original HTTP status (including 200) and body. Its ID is not automatically bound as a generated response for future `previous_response_id` use.

First binding filters targets by the explicit feature; an existing Session always uses its original target and fails if it lacks compaction. Feature changes invalidate existing bindings. Enabling a feature is the caller's declaration that the configured upstream/model supports it, not a live entitlement check. OpenAI-compatible third-party endpoints may not implement this native operation. There is no compaction protocol conversion, streaming, auto-trigger or retry. [Official compaction behavior](https://developers.openai.com/api/docs/guides/compaction) and [endpoint fields](https://developers.openai.com/api/reference/resources/responses/methods/compact) were checked for this implementation.

Compaction tests exercise the public SDK with substitute HTTP handlers, complete-window replay, Session ownership, failed and cancelled calls and concurrent/stale first-binding decisions. No live OpenAI compaction or account entitlement verification has been performed.

## Native Responses WebSocket

The provider implements `IResponsesWebSocketAdapter`. Set `Protocols = LlmProtocol.Responses` and `Features = LlmTargetFeatures.ResponsesWebSocket` on a native target. Enable compaction independently with the flag combination if needed. Existing Sessions keep the complete target; other providers and conversion decorators do not implicitly gain this transport. The original three-parameter HTTP constructor remains available with its previous default binding identity.

```csharp
var provider = new OpenAiLlmProvider(httpFactory); // default ClientWebSocket factory
// Optional explicit limits and borrowed custom handshake/proxy/header factory:
var customized = new OpenAiLlmProvider(httpFactory,
    new OpenAiResponsesWebSocketOptions
    {
        MaximumMessageBytes = 16 * 1024 * 1024,
        MaximumPendingRequests = 256,
        MaximumTrackedResponses = 4096,
        MaximumTrackedEventIds = 4096
    }, webSocketFactory: customFactory);
```

The target's HTTP(S) base path becomes WS(S) and ends in `/responses`; non-default ports are preserved. Each handshake uses that target's bearer credential. The named HTTP client's headers/proxy/resilience configuration is not automatically copied to `ClientWebSocket`; provide an `IOpenAiResponsesWebSocketFactory` for organization/project/custom headers or transport settings. The factory is borrowed; the returned socket belongs to the connection. Its `BindingIdentity` and non-default resource options participate in provider binding identity. Changing custom configuration invalidates old bindings. The default factory makes one handshake and disposes temporary sockets on failure; no retry or HTTP fallback occurs.

```csharp
await using var connection = await loki.LLM.Responses.OpenWebSocketAsync(
    publicModel, new LlmCallOptions { SessionId = sessionId }, cancellationToken);
await connection.SendAsync(new JsonObject
{
    ["type"] = "response.create", ["stream_id"] = "main",
    ["input"] = "Explain this code", ["store"] = false
}, cancellationToken);
await foreach (var item in connection.ReadEventsAsync(cancellationToken))
{
    ConsumeNativeEvent(item); // dispatch by JSON type/stream_id and inspect failures
    if (item.Type is "response.completed" or "response.failed" or "response.incomplete" or "error")
        break; // this example sent one request; multiplexed calls must drain every lane
}
```

Use one reader and explicit caller sends. `response.create` omits the model or supplies the public alias; the SDK sets the fixed actual model. Unknown JSON, opaque compaction/reasoning, native tool/call IDs, usage and errors stay unchanged. HTTP-only `stream`/`background` fields are rejected. Different named lanes can interleave; same-lane submissions stay associated with their own response IDs, queued/active states and optional sequence numbers. No model-specific scheduler or tool execution loop is introduced.

The connection distinguishes completed, failed and incomplete terminals, pending requests, control errors and connection-level errors. Duplicate/mismatched response identities, output after a terminal, inconsistent native status, backward sequences and closure before all generations finish fail explicitly. Unknown future events are preserved, including events with names resembling known output prefixes. An uncorrelated control error is never evidence that a generation finished. After a control whose acknowledgement cannot be proven independently, a later same-lane error without correlation IDs remains ambiguous even after the old generation ends: the raw error is returned and the new generation still requires its own terminal. Known control history is scoped to its lane; a control with unknown scope is handled conservatively. Locally unsent controls roll back that history. Native `response.steer` acknowledgement is not completion: automatic successors and explicit continuations after `response.steer.pending` are tracked; failed steering can legitimately omit its ID before acceptance. Returned required-input/tool IDs are not executed or filled by the SDK.

Limits are client memory policies, not upstream model capabilities. Messages are bounded in UTF-8 bytes, pending submissions in count, and remembered response/event identities in count. Raising them changes binding identity. An explicit client `event_id` cannot be reused on the same connection, including after its control completes: late or unknown-ID errors cannot consume a later uncorrelated generation. `MaximumTrackedEventIds` bounds that history and rejects excess new IDs before IO; a locally unsent request rolls back its reservation. On a response-identity limit error the connection aborts; the caller chooses whether and how to open another connection with suitable context. The SDK neither reconstructs history nor replays requests. Server connection/lane limits remain native errors. A socket/cache cannot restore connection-local server state; close/dispose belongs to the caller.

Offline in-memory WebSocket tests cover handshake address/authentication, public SDK Session ownership, multi-lane/FIFO transcripts, steering acceptance/rejection/automatic successors/required-input continuation, failure and truncated terminals, identity/sequence/status corruption, resource limits, byte fragmentation (including split Unicode), strict UTF-8, input snapshots, cancellation, close interruption and socket ownership. A cancelled or failed operation that has entered socket IO makes the connection unusable; queued cancellation is not replayed. No live OpenAI WebSocket, compaction or account-entitlement check has been performed. References: [WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode), [native event schemas](https://developers.openai.com/api/reference/resources/responses/websocket-events), [steering lifecycle](https://developers.openai.com/api/docs/guides/steering). Automatic history reconstruction and durable replay are not implemented.

For OpenAI Images Generations, install the independent `Asgard.Loki.Image.OpenAI` package. This LLM provider does not pull in image or audio dependencies.

## HTTP resource policy and terminal fidelity

The existing constructor signatures remain available. Optional HTTP policy is explicit:

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory, new OpenAiHttpOptions
{
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumBufferedTerminalEvents = 128
});
```

Both positive limits are per call. A combined overload accepts HTTP options, WebSocket options and an optional borrowed WebSocket factory. These are local resource limits, not model capability settings. The adapter preserves the explicit root prefix, actual model and caller-owned native fields; no model-name routing, endpoint fallback or cloud credential discovery is added.

Complete JSON and raw SSE bytes have strict UTF-8, duplicate-property and declared-length checks. Original HTTP/root errors retain status and body. Chat finish candidates and Responses completed/incomplete/failed/error terminals plus subsequent metadata wait for normal source EOF, content length, response/client disposal and final cancellation. `[DONE]` is consumed but does not stop reading; duplicate DONE, appended events, truncated frames and late errors fail. Incremental content before a terminal candidate remains incremental. Responses requires a native terminal with matching nonempty response ID/status; Chat requires DONE unless a native error terminates it. Failure/incomplete events remain native rather than becoming synthetic success.

Unknown fields and typed future metadata remain raw, including root business `error` fields on unknown typed events. This is a transport boundary, not a full Chat choice/tool-argument accumulator or a tool executor. Sparse choice/tool/item indices, call IDs and argument fragments are never renumbered or assembled. Endpoints must implement the declared protocol; wire preservation does not certify their model-specific reasoning/schema behavior. AWS Runtime/Mantle compatible variants remain a separate pending fixture milestone.

**Session migration:** the new `openai-http-v2` binding domain includes HTTP limits and named client. All predecessor OpenAI bindings, including WebSocket Sessions owned by this provider, require a new Session ID or expiry. Default and explicitly default HTTP options bind identically; changing policy/client invalidates an existing Session before HTTP. WebSocket framing policy and conversion-v25 algorithms are unchanged; a converter wrapping this provider incorporates its new native identity normally.

Two independently authored complete native manifests cover public SDK JSON/SSE request/response/frame snapshots, tools, reasoning/encrypted data, strict schema parameters, exact large integers and unknown fields. Fault cases cover EOF, length/byte limits, duplicate/invalid JSON, late native errors, disposal, final cancellation, early exit and cross-client target binding. No live OpenAI/model acceptance, Linux or remote Actions result is claimed.

Only explicit Responses lifecycle events register native response IDs; typed/SSE-named unknown events retain their raw nested `response` business objects without registering them as continuation targets. Empty unknown IDs no longer fail cache registration. This does not purge pre-existing cache entries or add history management. See the [core contract](../Asgard.Loki.LLM/README.md#http-responses-event-ownership).

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.

## Native Moderations

Declare `Protocols = LlmProtocol.Moderations` on an explicit complete target and call the existing LLM module's native client. No moderation model is chosen by the SDK. Register the configured model, credential, root and target separately from conversational routes.

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json;

// httpClientFactory, sessionStore, apiRoot, apiKey and configuredModerationModel are caller-owned.
var provider = new OpenAiLlmProvider(httpClientFactory, new OpenAiHttpOptions
{
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumModerationRequestBytes = 32 * 1024 * 1024
});
var target = new LlmTarget
{
    Id = "moderation-primary", ProviderId = provider.Id, BaseAddress = apiRoot,
    UpstreamModel = configuredModerationModel, CredentialId = "moderation-key",
    Protocols = LlmProtocol.Moderations
};
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("moderation-key", apiKey)],
    Routes = [new LlmRoute { Model = "classification", Targets = [target] }]
}).Build();
var result = await loki.LLM.Moderations.CreateAsync(new ModerationRequest
{
    Model = "classification",
    Parameters = new() { ["input"] = JsonSerializer.SerializeToElement(new[] { "first text", "second text" }) }
}, new LlmCallOptions { SessionId = "caller-classification-target-1" }, cancellationToken);
// Inspect original result.Data; the SDK does not make an application enforcement decision.
```

The provider appends only `moderations` under the configured HTTP(S) API root, preserving version/proxy prefixes. The public model maps to the target's actual model, while the native returned model/id remain unchanged. The required caller SessionId binds the full server/model/credential/adapter through the existing cache/coordinator; it does not store input text or implement conversational history. Classified result IDs are not registered as Responses continuation IDs. No conversion from Chat/Responses is performed.

`input` retains a scalar string, a nonempty string batch, or one nonempty array of native text/image_url parts. A text batch expects one positional result per string; a multimodal content array is one combined input and expects one result. URL and data-URL strings remain native; the SDK does not fetch media. Empty text remains empty, original whitespace/order/unknown fields are preserved and mixed/nested forms fail explicitly. No stream switch, input filter, model default, label or score is injected.

Results retain native flagged/categories/category_scores and optional category_applied_input_types, null/false fields, unknown categories/metadata and exact JSON numbers. Native id/model and complete result count are required. Known present labels are validated without filling missing ones; illicit and illicit/violent flags can be null. Known scores must be finite, without thresholding, normalization or score recalculation. Optional input-type metadata stays absent/null/native, and future label fields or input-type strings remain raw. Protocol correctness does not certify classification accuracy or opaque media interpretation.

MaximumModerationRequestBytes is optional and defaults effectively to 32 MiB; when omitted it is absent from serialized options, preserving previous default provider binding bytes. An explicit positive policy participates in the complete binding and invalidates incompatible old Sessions normally. MaximumBodyBytes bounds the entire native response/error (32 MiB by default). Both request bytes and target/model/credential are fixed before client creation. Authentication is API-Key-only; direct anonymous/external calls fail before dispatch. Unrelated default authentication headers are removed; caller-configured organization/project/other named-client headers remain available. Update the target Version if changing a named client's identity-affecting policy under the same name.

Native HTTP/HTTP200 errors retain actual StatusCode, complete Body bytes and raw ContentType through LlmUpstreamException, including binary errors or JSON errors labeled as plain text. ResponseBody is strict UTF-8 for byte errors; use Body for invalid binary text. Valid success requires complete strict UTF-8 JSON (application/json or a +json media type when supplied), unique members including unknown nested fields, compatible declared length and unchanged actual target/method. SSE is refused before reading a successful body. EOF, stream/response/client disposal and final cancellation precede result publication. No retry or target fallback is added.

The supplier has **408 offline cases**, including **91 native moderation cases**; the core has 20 additional moderation binding/protocol cases. Four independently authored full wire/result fixtures cover scalar, text batch, multimodal group and empty text. Boundary cases cover input/result association, label/null/unknown fidelity, binary/native errors, Unicode/duplicates/media/byte/length constraints, actual target/auth, resource closure/final cancellation and unchanged default binding identity. No real model/server/Key/media interpretation or classification-quality verification has been performed. Native contract references: [Moderations create](https://developers.openai.com/api/reference/resources/moderations/methods/create), [official request types](https://github.com/openai/openai-python/blob/main/src/openai/types/moderation_create_params.py) and [official result types](https://github.com/openai/openai-python/blob/main/src/openai/types/moderation.py).
