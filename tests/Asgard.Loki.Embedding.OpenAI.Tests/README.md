# OpenAI Embeddings protocol tests

xUnit v3 tests use public `AddEmbedding` / `loki.Embedding.EmbedAsync` with the actual `OpenAiEmbeddingAdapter` and substitute HTTP handlers. Independent complete request goldens cover scalar/batch text and token inputs, float/Base64 encodings and caller-selected models. Synthetic native results retain original order/index association, unknown values and missing usage.

Validation covers dimensions/formats, missing/duplicate/foreign indices, incomplete vectors, malformed Base64/JSON, duplicate members, native HTTP/business errors, cancellation, API-root/auth headers, secret-free options and borrowed-factory/response lifetime. No live API, real Key, Redis, tokenizer or embedding-quality evaluation is used.

The [Jina-compatible fixtures](Fixtures/jina-compatible.json) describe only the canonical text/indexed numeric subset accepted by this adapter. They retain native options, raw fields/usage and index associations; object inputs, per-token/multivector output and bit dimensions have explicit boundaries. Packed arrays without declared dimensions remain raw numbers, not certified bit embeddings. Sparse samples do not establish a deployed sparse model or a native Jina implementation.

`m3e-profile.json` retains canonical scalar/batch model/input, reordered float vectors, optional root/item fields, usage/unknown metadata and raw HTTP errors. Snapshot and duplicate-index regressions exercise public SDK wiring. These fixtures do not establish a hosted Moka service, token/Base64 support or model availability.

[Workers AI vectors](Fixtures/cloudflare-profile.json) retain the account-scoped canonical OpenAI Embeddings operation, full scalar/batch wire bodies and native errors; incompatible `ai/run` envelopes remain rejected. [Explicit compatible profiles](Fixtures/compatible-profiles.json) retain independent root/model/Key/Id/client configuration. They do not certify brand routing, model options, anonymous auth or hosted Moonshot embedding availability.

## Test ownership

Infrastructure.Tests owns common HttpBodyReader length, EOF and stream-disposal matrices. This suite retains adapter budget wiring, exact raw results, encoding/media/native errors, target/header isolation, immutable requests and outer client-disposal/final-cancellation behavior. Expected values are independently authored; no production converter generates them.
