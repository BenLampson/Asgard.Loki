# Asgard.Loki.LLM.OpenAI.Tests

93 offline tests for the actual OpenAI LLM adapter: HTTP/JSON, fragmented SSE and a shared parameter matrix through the public Loki client. Final HTTP assertions cover native tool/reasoning items, schema parameters, unknown fields, false/zero/null, request immutability, route/model mapping and reserved-field rejection.

xUnit v3 / Microsoft.Testing.Platform with stub HTTP and `MemoryDistributedCache`. Tests use no real provider credentials or external services. OpenAI has not been live-verified; wire preservation does not prove model behavior. See [verification scope](../../docs/llm-parameter-verification.md).
