# Rerank core tests

xUnit v3 tests exercise `LokiClientBuilder.AddRerank` and `loki.Rerank.RerankAsync` with custom in-memory adapters. They cover eager configuration validation/snapshots, native query/documents/result JSON, original score/index/array preservation, exact model aliases, independent and concurrent target selection, cancellation, single-attempt error propagation and borrowed resource ownership.

These are core-contract tests. Supplier HTTP payloads, index/score validity and actual relevance quality remain separate work. No HTTP, live provider Key, Redis, tokenizer, embedding module or retrieval pipeline is used.
