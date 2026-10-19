# Cohere native rerank tests

The 120 xUnit v3 offline cases exercise `AddRerank` and `loki.Rerank.RerankAsync` plus direct adapter calls. Four independently authored request/wire/response fixtures cover fixed v1 text/object inputs and v2 basic/explicit settings. Expected wire JSON is not generated from production mappings.

Assertions cover full native JSON retention, original index/text/object-field association, top-n counts, optional/false/null/zero values, exact integer/score bounds and raw numeric representation, request snapshots, native status/error bodies, malformed/truncated/duplicate JSON, successful SSE rejection before body reads, cancellation and response/factory ownership. A native result view is not evidence of actual model ranking quality.

Only substitute HTTP handlers, fragmented in-memory streams and custom factories are used. No provider Key, Redis, live network or real Cohere model is required. Local verification is recorded separately from PR/release GitHub CI.
