# Asgard.Loki.LLM

Responses WebSocket has optional contracts and SDK connection/Session enforcement. `loki.LLM.Responses.OpenWebSocketAsync(model, options)` requires `LlmTargetFeatures.ResponsesWebSocket`, the Responses protocol, and an `IResponsesWebSocketAdapter`. **The bundled providers do not yet implement the native transport**; this checkpoint is usable with a caller-supplied adapter and does not imply automatic HTTP-to-WebSocket conversion. Legacy custom `IResponsesClient` implementations retain the default unsupported method.

The returned `IResponsesWebSocketConnection` belongs to the caller. Use `await using`, explicitly send native JSON events, and enumerate all server events with a single reader. Concurrent writes are serialized, while native `stream_id` lanes are preserved. On `response.create`, omit `model` or use the public alias passed to `OpenWebSocketAsync`; the SDK writes the fixed upstream model. `stream` and `background` are rejected rather than removed. Input is copied before waiting, and unknown/native opaque fields are preserved. Future control events remain native; the SDK does not execute tools.

Every send validates the original cached binding without creating a replacement. Known lifecycle events register response IDs to the same Session; creation/steering `previous_response_id` and explicit `response.cancel.response_id` require that ownership, including across HTTP and WebSocket calls. `response.steer` requires a nonempty response ID and preserves its native input; the SDK adds no steering or tool loop. Connection disposal does not delete the logical Session or dispose borrowed factories/stores. Session loss, changed bindings, cache errors, failed handshakes and cancellation propagate without retry/failover. Closing a socket does not allow the cache to restore its server-side state. Native framing and transport terminal validation remain the adapter's responsibility and are the next implementation checkpoint. See the current [WebSocket event schemas](https://developers.openai.com/api/reference/resources/responses/websocket-events).

Native optional operations are advertised independently of protocol families. `loki.LLM.Responses.CompactAsync` uses `ResponseCompactionRequest` / `ResponseCompaction`; it requires a target declaring `LlmTargetFeatures.ResponsesCompaction` and a provider implementing `IResponsesCompactionAdapter`. The complete returned window stays opaque and caller-controlled. Optional `PreviousResponseId` is checked against Session ownership; a compaction result ID is not registered as a generated-response ID. Unsupported existing targets fail without rerouting. The default interface method keeps older custom `IResponsesClient` implementations usable and reports compaction as unsupported. See the [root compaction example](../../README.md#native-responses-compaction).

Independent SDK contracts and orchestration for Chat Completions, Responses, Claude Messages and Gemini generateContent.

## Supported

- Complete JSON results and streamed protocol events, including unknown fields, tools, reasoning, usage and errors.
- Public model aliases mapped to complete upstream targets: provider instance, server, actual model, credential and configuration version.
- Shared Session bindings and original response-ID ownership.
- `IDistributedCache` storage with an explicit `ILlmSessionLock` / `ILlmSessionLease` coordination contract.
- `AddLLM()` and the C# 14 `.LLM` extension property.
- Native `.ClaudeMessages` and `.Gemini` clients, each with independent request/result/event contracts.
- `ILlmProvider.BindingIdentity` participates in target fingerprints; old bindings created without it require a new Session or caller-managed expiration.

Register providers separately, currently `Asgard.Loki.LLM.Volcengine`, `Asgard.Loki.LLM.OpenAI`, `Asgard.Loki.LLM.Anthropic` or `Asgard.Loki.LLM.Google`. This package does not reference any provider, the client implementation, or another capability.

First binding must be coordinated across processes. Plain cache Get/Set is not atomic. Missing/changed bindings fail; generation is not silently retried or rerouted. Cache/coordinator ownership remains with the caller.

See the root README for initialization and `docs/architecture.md` for the extension contract. Native WebSocket transport and durable request replay are not implemented.

Optional explicit conversion lives in `Asgard.Loki.LLM.Conversion`, which depends on this package. Native providers never opt into it automatically. Converted results expose upstream JSON and diagnostics through `Metadata.Conversion`; see the conversion package matrix for supported fields and hard failures.
