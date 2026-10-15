# Asgard.Loki.LLM.Volcengine.Tests

93 offline tests for the actual Volcengine LLM adapter: HTTP/JSON, fragmented SSE and a shared parameter matrix through the public Loki client. Final HTTP assertions cover native tool/reasoning items, schema parameters, unknown fields, false/zero/null, request immutability, route/model mapping and reserved-field rejection.

xUnit v3 / Microsoft.Testing.Platform with stub HTTP and `MemoryDistributedCache`. Tests use no real provider credentials or external services. Real Ark behavior is checked only by the separate integration project; see [verification scope and known tool-choice failures](../../docs/llm-parameter-verification.md).
