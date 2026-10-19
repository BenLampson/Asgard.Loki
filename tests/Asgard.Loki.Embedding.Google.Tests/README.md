# Google Embedding protocol tests

xUnit v3 tests use the public Embedding module and actual Google native adapter with substitute HTTP handlers. Six independent complete request goldens cover single/synchronous batch, legacy/current/ProtoJSON-alias config, per-item model/task/title/dimensions and multimodal JSON transport. Response fixtures preserve original order, zero/unknown usage, false/null fields and finite numerical values without synthetic indices.

Rejection cases cover incomplete content, mixed operations, foreign batch models, precise dimensionality and conflicting aliases/envelopes, missing/extra/swapped results, nonfinite/malformed values, HTTP/business errors, invalid/truncated/duplicate JSON, cancellation and borrowed-factory/response ownership. Unexpected SSE fails before reading. No model tokenizer, live media parser, network, real API Key, Redis or vector-quality evaluation is used.
