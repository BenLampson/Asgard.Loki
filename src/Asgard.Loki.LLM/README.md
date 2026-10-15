# Asgard.Loki.LLM

Independent LLM contracts and orchestration for Chat Completions and Responses.

## Supported

- Complete JSON results and streamed protocol events, including unknown fields, tools, reasoning, usage and errors.
- Public model aliases mapped to complete upstream targets: provider instance, server, actual model, credential and configuration version.
- Shared Session bindings and original response-ID ownership.
- `IDistributedCache` storage with an explicit `ILlmSessionLock` / `ILlmSessionLease` coordination contract.
- `AddLLM()` and the C# 14 `.LLM` extension property.

Register providers separately, currently `Asgard.Loki.LLM.Volcengine` or `Asgard.Loki.LLM.OpenAI`. This package does not reference either provider, the client implementation, or another capability.

First binding must be coordinated across processes. Plain cache Get/Set is not atomic. Missing/changed bindings fail; generation is not silently retried or rerouted. Cache/coordinator ownership remains with the caller.

See the root README for initialization and `docs/architecture.md` for the extension contract. Native WebSocket transport and durable request replay are not implemented.
