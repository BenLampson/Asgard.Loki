# Asgard.Loki.LLM.Google.Tests

Offline xUnit v3 tests exercise the actual Gemini Developer API provider through the public SDK client. Shared native tests check the versioned model/action URL, `x-goog-api-key`, native contents and opaque thought signatures, generation extensions, body omission of model/stream, fragmented SSE, unknown events, errors, cancellation, Session continuation and missing terminal events. Additional tests verify every observed candidate must finish and preserve blocked-prompt feedback without fabricating a candidate.

All HTTP uses substitute handlers; cache tests use `MemoryDistributedCache` and test-only coordination. No live Gemini account, model entitlement, network or Redis verification is performed. See [testing](../../docs/testing.md).

Terminal regressions cover repeated candidate finishes, data after errors or blocked prompts, and valid usage-only tails after candidate completion.
