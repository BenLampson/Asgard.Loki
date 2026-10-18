# Asgard.Loki.LLM.Anthropic.Tests

Offline xUnit v3 tests exercise the actual Claude provider through the public SDK client. Shared native tests cover request-local headers and final URI/model/stream, unknown parameters and zero/false/null/empty JSON, images/tools/results, thinking signatures, unknown fragmented SSE, HTTP and native errors, cancellation, invalid fields, Session continuation and incomplete streams.

All HTTP uses substitute handlers. Cache tests use `MemoryDistributedCache` and an explicit test-only coordinator. Tests do not require keys, network or Redis and do not establish live Anthropic model/account acceptance. See [testing](../../docs/testing.md).
