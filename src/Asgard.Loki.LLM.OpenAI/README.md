# Asgard.Loki.LLM.OpenAI

OpenAI LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |
| Native Responses compaction | Yes, explicit target feature | No |
| Native Responses WebSocket | Explicit duplex connection | Native JSON events; named lanes |

Create `OpenAiLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Configure targets with a `/v1/` base address, actual model and credential. The default provider ID is `openai`; the named HTTP client is `Asgard.Loki.LLM.OpenAI`.

Preserves extension parameters, tools, reasoning items, cache controls, original response IDs, usage, error events and unknown JSON. Session routing uses the shared LLM module. Configure project/organization defaults through the named HTTP client if needed. Disable POST retries and hedging.

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

`ResponseCompaction.Data` contains the complete native result, including usage and unknown fields. `Output` exposes the full returned array, including retained messages, tool IDs and opaque encrypted compaction items. Do not extract only the encrypted item or discard retained items. An ordinary response, missing output array or explicit error is rejected as an invalid compaction result. Its ID is not automatically bound as a generated response for future `previous_response_id` use.

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
