# OpenAI Embeddings protocol tests

xUnit v3 tests use the public `AddEmbedding` / `loki.Embedding.EmbedAsync` path and an actual `OpenAiEmbeddingAdapter` with substitute HTTP handlers. Eight independently authored complete request goldens cover four input forms and two encodings. Synthetic native response fixtures cover original order/index association, preserved extensions and missing usage.

Rejection/lifecycle cases cover invalid inputs, dimensions and formats, missing/duplicate/foreign indices, incomplete vectors, malformed Base64/JSON and duplicate fields, HTTP/business errors, cancellation, API-root/authentication headers, secret-free options serialization and borrowed-factory/response lifetime. No live API, real Key, Redis, model tokenizer or embedding-quality evaluation is used.
