# Asgard.Loki.LLM

Responses WebSocket has optional contracts and SDK connection/Session enforcement. `loki.LLM.Responses.OpenWebSocketAsync(model, options)` requires `LlmTargetFeatures.ResponsesWebSocket`, the Responses protocol, and an `IResponsesWebSocketAdapter`. The [OpenAI provider](../Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket) supplies a native transport; custom adapters remain supported. Other providers and converting decorators do not implicitly gain this feature, and HTTP calls do not switch transports automatically. Legacy custom `IResponsesClient` implementations retain the default unsupported method.

The returned `IResponsesWebSocketConnection` belongs to the caller. Use `await using`, explicitly send native JSON events, and enumerate all server events with a single reader. Concurrent writes are serialized, while native `stream_id` lanes are preserved. On `response.create`, omit `model` or use the public alias passed to `OpenWebSocketAsync`; the SDK writes the fixed upstream model. `stream` and `background` are rejected rather than removed. Input is copied before waiting, and unknown/native opaque fields are preserved. Future control events remain native; the SDK does not execute tools.

Every send validates the original cached binding without creating a replacement. Known lifecycle events register response IDs to the same Session; creation/steering `previous_response_id` and explicit `response.cancel.response_id` require that ownership, including across HTTP and WebSocket calls. `response.steer` requires a nonempty response ID and preserves its native input; the SDK adds no steering or tool loop. Connection disposal does not delete the logical Session or dispose borrowed factories/stores. Session loss, changed bindings, cache errors, failed handshakes and cancellation propagate without retry/failover. State is checked again after awaited Session validation, so operations still awaiting the cache do not enter native send/read after the caller closes or disposes the connection. Closing a socket does not allow the cache to restore its server-side state. Native framing and terminal validation remain the adapter's responsibility; the OpenAI provider implements these checks. See the current [WebSocket event schemas](https://developers.openai.com/api/reference/resources/responses/websocket-events).

Native optional operations are advertised independently of protocol families. `loki.LLM.Responses.CompactAsync` uses `ResponseCompactionRequest` / `ResponseCompaction`; it requires a target declaring `LlmTargetFeatures.ResponsesCompaction` and a provider implementing `IResponsesCompactionAdapter`. The complete returned window stays opaque and caller-controlled. Optional `PreviousResponseId` is checked against Session ownership; a compaction result ID is not registered as a generated-response ID. Unsupported existing targets fail without rerouting. The default interface method keeps older custom `IResponsesClient` implementations usable and reports compaction as unsupported. See the [root compaction example](../../README.md#native-responses-compaction).

Independent SDK contracts and orchestration for Chat Completions, Responses, Claude Messages, Gemini generateContent, Ollama Chat and Ollama generate.

## Explicit credential kinds

`new LlmCredential(id, apiKey)` keeps the existing nonempty API Key contract. `LlmCredential.Anonymous(id)` is a separate, explicit no-authentication identity. It requires a stable nonempty ID, stores no Key, and throws when `ApiKey` is read. This prevents old native adapters, including direct OpenAI HTTP/SSE/compaction/WebSocket calls, from sending an empty Bearer value. JSON and `ToString` remain secret-free; the default API Key metadata shape is unchanged.

A provider opts into `ILlmCredentialSupport.SupportsCredential(LlmCredentialKind)` for each supported native authentication kind. Existing providers without that optional interface accept API Keys only. Module registration validates every configured target, including unused alternatives, before Session/cache/HTTP work; no automatic downgrade, Key borrowing or endpoint inference occurs. A converting decorator delegates support to its fixed upstream and cannot extend it through loss consent.

```csharp
var localIdentity = LlmCredential.Anonymous("local-instance");
// Register with the native Ollama provider or an application-owned provider
// that explicitly declares and implements LlmCredentialKind.Anonymous.
```

API Key fingerprints retain their previous byte representation, allowing valid existing Sessions to resume. Anonymous bindings use a separate fingerprint domain while retaining server, actual model, credential ID, protocol, provider implementation and configuration version. Authentication-kind changes or any of those target changes reject an old Session; they do not cause target reselection. Configuration collections are snapshotted, and concurrent first binding still uses the explicit distributed coordination contract.

This is an LLM-owned credential foundation; cloud token refresh/signing and other capabilities have separate contracts. Providers remain responsible for HTTP/header behavior; the core does not rewrite caller-owned HTTP defaults or probe servers. The separate [Ollama provider](../Asgard.Loki.LLM.Ollama/README.md) implements native Chat JSON/NDJSON and rejects default Authorization for anonymous targets. The credential prerequisite has 40 offline regressions; native Ollama adds 159 independent Chat/generate wire/result/event and transport/session boundary cases. No real authentication/model call was performed.

## External authentication identity

`LlmCredential.External(id, principalIdentity)` declares a stable external principal/reference without storing an API Key or short-lived token. Both identifiers are required; `PrincipalIdentity` is available only for this kind, and `ApiKey` throws. Default JSON and `ToString` expose the credential ID/kind without serializing the principal or any secret. It is separate from `Anonymous`, which still means no authentication.

```csharp
var identity = LlmCredential.External("cloud-credential", "configured-tenant/principal");
// A provider must explicitly support LlmCredentialKind.External and borrow
// the application's provider-specific token/signing source.
```

Providers opt in through `ILlmCredentialSupport`; legacy providers remain API-Key-only, and the existing Ollama provider does not support this kind. Converting decorators delegate their fixed upstream's declaration. The core does not acquire/refresh tokens, read environment credentials or add cloud dependencies. Direct legacy Key-only operations fail before HTTP/WebSocket dispatch instead of sending an empty credential.

External fingerprints use a separate domain with structured target/principal/provider components, so newline-containing references cannot move separators and collide with implementation identities. Changing principal, authentication kind, server/model/credential identity or provider configuration rejects an old Session; stable principal identity allows a provider-owned source to refresh short-lived tokens without putting them in this fingerprint. API Key and anonymous fingerprint bytes are unchanged and verified using independently constructed legacy cache entries. Concurrent first binding and snapshots retain the existing explicit coordination contract.

This prerequisite has 38 new offline cases (28 core, four conversion and six direct OpenAI boundaries). It is usable by caller-owned adapters and the independent [Azure provider](../Asgard.Loki.LLM.Azure/README.md), whose native complete JSON endpoint/token transport has 86 additional offline cases and whose separate SSE checkpoint adds 69 cases. Azure native Chat/Responses SSE preserves delayed annotations and terminal boundaries; compact/WebSocket remain pending. No cloud login, actual token refresh or live model call is claimed.

The independent [Vertex provider](../Asgard.Loki.LLM.GoogleVertex/README.md) additionally uses External identities for native Gemini/Claude JSON/SSE. Its target model is the full project/location/publisher-model or tuned-endpoint resource, so existing complete-target Sessions bind that identity without new core protocols. Authentication refresh remains source-owned; Native candidate/block terminals and usage tails wait for EOF/disposal; other Vertex dialects remain pending. 389 offline cases verify native wire/events, ProtoJSON numeric forms, Claude body version/tools/signatures, identity and lifecycle, not cloud login or live acceptance. Claude uses the existing explicit ClaudeMessages protocol and a full Anthropic publisher resource; its native SSE additionally preserves raw block/tool association, signatures and cumulative usage, with delayed terminal/error handling through EOF and disposal. Default omitted Claude options preserve preceding Gemini provider binding bytes; explicit version or block-policy changes are fingerprinted. Omitted Claude block policy retains existing Gemini/Claude JSON bindings.

The same Vertex supplier package additionally exposes `GoogleVertexExpressLlmProvider`, a separate API-Key-only Gemini JSON/SSE adapter for explicit `publishers/google/models/{model}` short resources. It needs no token source and adds no core protocol. Standard provider authentication/binding stays unchanged; 67 Express cases verify native data, identity, resource limits and explicit conversion integration without live credentials.

## Native Ollama operations

`loki.LLM.OllamaChat.CompleteAsync` / `StreamAsync` use `OllamaChatRequest`, `OllamaChatResponse` and `OllamaChatEvent` through optional `IOllamaChatAdapter`. Targets explicitly declare `LlmProtocol.OllamaChat`; existing default target flags remain unchanged. Messages and native extension JSON are snapshotted before awaited Session resolution. Only actual model and method stream mode change. Native tool IDs/history, images, thinking, `format` schema, `options`, unknown values and counters remain intact; there is no conversion to an OpenAI envelope.

The provider retains native errors and validates NDJSON identity/terminal boundaries without aggregating tool calls or executing them. Stream done/error records wait for normal EOF, declared length and transport disposal. Partial records remain incremental; truncation, late data, invalid Unicode and resource failures propagate without fabricated completion or retry. `OllamaChatEvent.IsTerminal` includes errors; check `IsError` for failed generation. Native generate has its own client described below; Ollama conversion remains follow-up work. Provider initialization, resource limits, support matrix and offline/live status are documented in its README.

## Native JSON snapshot encoding

Public native clients validate original DOM strings/property names and extension `JsonElement` strings **before** serialization and awaited Session resolution. This covers Chat, Responses, Claude, Gemini and both Ollama clients, including complete/streamed calls, plus Responses compaction. Bound Responses WebSocket sends apply the same check before snapshot/cache/send. Unpaired UTF-16 surrogates and malformed JSON string escapes fail; they cannot become replacement characters that change tool IDs, error text or unknown native fields. Streaming validation begins when enumeration starts.

Registration and fingerprint creation likewise validate model/target/credential/provider identities and Key text, while call resolution checks original model/Session/previous-response IDs. Valid target/key fingerprints and serialized metadata stay unchanged; no new protocol flag, package dependency or conversion algorithm version is introduced. Existing valid Sessions remain usable. Borrowed stores/coordinators/factories retain their ownership.

Valid Unicode is not normalized, filtered or rewritten: combining characters, emoji, BOM/NUL/CRLF, unknown fields, exact JSON numbers and optional null/false values retain their native form. This is an encoding boundary, not a model/schema capability validator. Caller-supplied custom `JsonValue`/converter serialization retains its caller-owned policy; the SDK does not reflect over business objects or execute their getters as a separate validation pass. A custom adapter or direct provider call still owns any serialization it performs outside the public-client snapshot.

Sixty-five new offline regressions include eight public Claude/Gemini conversion cases first observed failing before the fix, 49 LLM-core cases and eight Ollama extension cases. They directly construct raw DOM requests rather than allowing a test deserializer to repair them first. No live model call is made.

## Supported operations

Native `.OllamaGenerate.GenerateAsync` / `StreamAsync` are separate from Chat, using `OllamaGenerateRequest`, `OllamaGenerateResponse`, `OllamaGenerateEvent` and optional `IOllamaGenerateAdapter`. Configure `LlmProtocol.OllamaGenerate` explicitly. Only the routed model is named in the request; native prompt/suffix/system/template/context and generation settings live in extension JSON, preserving omitted/empty/null values without injecting Chat structure. Context is neither persisted nor replayed automatically. Generate keeps complete-target Session affinity and cannot change a Chat Session's protocol. See the [Ollama generate example](../Asgard.Loki.LLM.Ollama/README.md#native-generate).

- Complete JSON results and streamed protocol events, including unknown fields, tools, reasoning, usage and errors.
- Public model aliases mapped to complete upstream targets: provider instance, server, actual model, credential and configuration version.
- Shared Session bindings and original response-ID ownership.
- `IDistributedCache` storage with an explicit `ILlmSessionLock` / `ILlmSessionLease` coordination contract.
- `AddLLM()` and the C# 14 `.LLM` extension property.
- Native `.ClaudeMessages`, `.Gemini`, `.OllamaChat` and `.OllamaGenerate` clients, each with independent request/result/event contracts.
- `ILlmProvider.BindingIdentity` participates in target fingerprints; old bindings created without it require a new Session or caller-managed expiration.

Register providers separately, currently `Asgard.Loki.LLM.Volcengine`, `Asgard.Loki.LLM.OpenAI`, `Asgard.Loki.LLM.Anthropic`, `Asgard.Loki.LLM.Google`, `Asgard.Loki.LLM.Ollama`, `Asgard.Loki.LLM.Azure` or `Asgard.Loki.LLM.GoogleVertex`. This package does not reference any provider, the client implementation, or another capability.

First binding must be coordinated across processes. Plain cache Get/Set is not atomic. Missing/changed bindings fail; generation is not silently retried or rerouted. Cache/coordinator ownership remains with the caller.

See the root README for initialization and `docs/architecture.md` for the extension contract. Native WebSocket transport is provider-owned; durable request replay is not implemented.

Optional explicit conversion lives in `Asgard.Loki.LLM.Conversion`, which depends on this package. Native providers never opt into it automatically. Converted results expose upstream JSON and diagnostics through `Metadata.Conversion`; see the conversion package matrix for supported fields and hard failures.

Vertex also exposes a separate explicit MaaS Chat complete-JSON provider in its existing supplier package. It borrows the existing Vertex token source, binds the full project/location endpoint and publisher/model, and preserves native tool/reasoning/schema/extension JSON. MaaS SSE remains pending; no new core protocol, supplier dependency or live acceptance is implied.
