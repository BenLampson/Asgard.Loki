# Asgard.Loki.LLM

Responses WebSocket has optional contracts and SDK connection/Session enforcement. `loki.LLM.Responses.OpenWebSocketAsync(model, options)` requires `LlmTargetFeatures.ResponsesWebSocket`, the Responses protocol, and an `IResponsesWebSocketAdapter`. The [OpenAI provider](../Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket) supplies a native transport; custom adapters remain supported. Other providers and converting decorators do not implicitly gain this feature, and HTTP calls do not switch transports automatically. Legacy custom `IResponsesClient` implementations retain the default unsupported method.

The returned `IResponsesWebSocketConnection` belongs to the caller. Use `await using`, explicitly send native JSON events, and enumerate all server events with a single reader. Concurrent writes are serialized, while native `stream_id` lanes are preserved. On `response.create`, omit `model` or use the public alias passed to `OpenWebSocketAsync`; the SDK writes the fixed upstream model. `stream` and `background` are rejected rather than removed. Input is copied before waiting, and unknown/native opaque fields are preserved. Future control events remain native; the SDK does not execute tools.

Every send validates the original cached binding without creating a replacement. Known lifecycle events register response IDs to the same Session; creation/steering `previous_response_id` and explicit `response.cancel.response_id` require that ownership, including across HTTP and WebSocket calls. `response.steer` requires a nonempty response ID and preserves its native input; the SDK adds no steering or tool loop. Connection disposal does not delete the logical Session or dispose borrowed factories/stores. Session loss, changed bindings, cache errors, failed handshakes and cancellation propagate without retry/failover. State is checked again after awaited Session validation, so operations still awaiting the cache do not enter native send/read after the caller closes or disposes the connection. Closing a socket does not allow the cache to restore its server-side state. Native framing and terminal validation remain the adapter's responsibility; the OpenAI provider implements these checks. See the current [WebSocket event schemas](https://developers.openai.com/api/reference/resources/responses/websocket-events).

Native optional operations are advertised independently of protocol families. `loki.LLM.Responses.CompactAsync` uses `ResponseCompactionRequest` / `ResponseCompaction`; it requires a target declaring `LlmTargetFeatures.ResponsesCompaction` and a provider implementing `IResponsesCompactionAdapter`. The complete returned window stays opaque and caller-controlled. Optional `PreviousResponseId` is checked against Session ownership; a compaction result ID is not registered as a generated-response ID. Unsupported existing targets fail without rerouting. The default interface method keeps older custom `IResponsesClient` implementations usable and reports compaction as unsupported. See the [root compaction example](../../README.md#native-responses-compaction).

Independent SDK contracts and orchestration for Chat Completions, Responses, Claude Messages and Gemini generateContent.

## Explicit credential kinds

`new LlmCredential(id, apiKey)` keeps the existing nonempty API Key contract. `LlmCredential.Anonymous(id)` is a separate, explicit no-authentication identity. It requires a stable nonempty ID, stores no Key, and throws when `ApiKey` is read. This prevents old native adapters, including direct OpenAI HTTP/SSE/compaction/WebSocket calls, from sending an empty Bearer value. JSON and `ToString` remain secret-free; the default API Key metadata shape is unchanged.

A provider opts into `ILlmCredentialSupport.SupportsCredential(LlmCredentialKind)` for each supported native authentication kind. Existing providers without that optional interface accept API Keys only. Module registration validates every configured target, including unused alternatives, before Session/cache/HTTP work; no automatic downgrade, Key borrowing or endpoint inference occurs. A converting decorator delegates support to its fixed upstream and cannot extend it through loss consent.

```csharp
var localIdentity = LlmCredential.Anonymous("local-instance");
// Register it only with an application-owned provider that explicitly supports
// LlmCredentialKind.Anonymous and implements the actual native no-auth policy.
```

API Key fingerprints retain their previous byte representation, allowing valid existing Sessions to resume. Anonymous bindings use a separate fingerprint domain while retaining server, actual model, credential ID, protocol, provider implementation and configuration version. Authentication-kind changes or any of those target changes reject an old Session; they do not cause target reselection. Configuration collections are snapshotted, and concurrent first binding still uses the explicit distributed coordination contract.

This is an LLM-owned credential foundation, not a native Ollama operation, cloud token refresh/signing contract or a common coordinator for other capabilities. Providers remain responsible for HTTP/header behavior; the core does not rewrite caller-owned HTTP defaults or probe servers. Native Ollama JSON/NDJSON calls and transport/header isolation require their own provider checkpoint. Forty new offline regressions include 25 core cases, nine conversion cases and six direct OpenAI boundary cases; no real authentication/model call was performed.

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

See the root README for initialization and `docs/architecture.md` for the extension contract. Native WebSocket transport is provider-owned; durable request replay is not implemented.

Optional explicit conversion lives in `Asgard.Loki.LLM.Conversion`, which depends on this package. Native providers never opt into it automatically. Converted results expose upstream JSON and diagnostics through `Metadata.Conversion`; see the conversion package matrix for supported fields and hard failures.
