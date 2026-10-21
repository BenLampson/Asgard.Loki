# Embedding core tests

xUnit v3 tests run through the public `LokiClientBuilder.AddEmbedding` and `loki.Embedding` extensions with in-memory custom adapters. They cover native payload/result snapshots, exact model routing, per-route/per-client and concurrent target selection, registration failures, cancellation, single-attempt errors and caller-owned adapter lifetime.

These tests use no keys, HTTP, Redis or LLM Session. Native OpenAI/Google protocol and actual vector/model verification are separate work; synthetic native-shaped JSON does not certify model acceptance or embedding quality.

Tests cover the unchanged named text constructor, byte-buffer copying/original status/content type, and lossless binary error access with explicit strict UTF-8 text failure. No network or supplier acceptance is involved.
