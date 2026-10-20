# OpenAI-compatible rerank protocol tests

97 xUnit v3 cases use actual native adapters and substitute HTTP, including six complete public SDK goldens for explicit Anonymous/Bearer modes and text/opaque JSON candidates. Independently authored [native wire fixtures](Fixtures/native.json) precede implementation. Native scores can be negative or greater than one; root identity/usage may be missing or null, and original model/score precision/unknown fields remain raw.

Positive tests cover exact model mapping, full query/documents/settings, input snapshots, nullable/default flags, unchanged empty/whitespace text, huge exact top_n and required explicit null echo. Rejection cases cover invalid structures/flags, precise fractional/duplicate/foreign indices, incomplete counts, wrong/missing/null echoes, missing/changed original object fields, malformed score aliases and usage fields, foreign score arrays and malformed JSON.

Raw transport/target cases cover binary errors, raw malformed content types, strict unknown-field Unicode/duplicates, declared length/byte limits, actual target/header isolation, pre/factory/late cancellation, resource disposal failures and fast SSE rejection. No production converter creates expectations. No real Key, server process, HTTP service, Redis, tokenizer, template, media fetching or model/relevance/opaque-content semantic verification is used. Other suppliers retain independent protocol validation.

Three additional full public SDK profile cases verify explicit generic/Moonshot-style/custom roots, independent Bearer/Id/model/client and original native wire. These are protocol checks only; no hosted model or vendor availability is established.
