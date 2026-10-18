# Asgard.Loki.LLM

Responses WebSocket extension contracts are present as a separate small checkpoint: `IResponsesWebSocketAdapter` and `IResponsesWebSocketConnection` define native duplex operations. The built-in `OpenWebSocketAsync` still reports unsupported until the connection/session wrapper and provider transport are implemented; these contracts do not change the implemented support matrix or imply automatic HTTP-to-WebSocket conversion.

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
