# Asgard.Loki.LLM.Volcengine.Tests

244 offline tests for the actual Volcengine LLM adapter: HTTP/JSON, fragmented SSE and a shared parameter matrix through the public Loki client. Final HTTP assertions cover native tool/reasoning items, schema parameters, unknown fields, false/zero/null, request immutability, route/model mapping and reserved-field rejection.

xUnit v3 / Microsoft.Testing.Platform with stub HTTP and `MemoryDistributedCache`. Tests use no real provider credentials or external services. Real Ark behavior is checked only by the separate integration project; see [verification scope and known tool-choice failures](../../docs/llm-parameter-verification.md).

Independent full caller/wire/result/error JSON and separately authored literal SSE/expected-frame resources verify the actual supplier implementation without generating snapshot expectations from SDK output. Native tools and reverse outputs, reasoning/encrypted data, schema, precise values, unknown fields, incomplete/length, usage and transport IDs remain original. Chat extensions with a future non-string type are data; Responses known type/identity/status stays protocol-owned.

The executable includes 114 additional native HTTP/fault/Session cases. Tests prove finish/global-DONE separation, continued EOF checking after DONE, held Responses terminal/usage/unknown tails, duplicate/missing/changed identity/late generation failures, exact binary/native errors, original Unicode/duplicate/alias rejection before factory, request snapshots, byte/length/tail/choice limits, sparse indices without arrays, async/sync/client disposal and final cancellation/target guards. No held terminal/result escapes these failures. Model-root/source selectors are explicit, with no suffix/host inference. Policy/client/root/model drift rejects cached Sessions; identical instances recover native response ownership using MemoryDistributedCache and the explicit TestSessionLock.

All fake HTTP is offline. No Key/environment/auth-file lookup, provider request, Redis service, model acceptance or parameter effectiveness is involved. Existing real integration configuration/entrypoint and known upstream tool-choice failure remain unchanged.

## Published Bot native client

Thirty-seven additional cases exercise VolcengineBotClient using independently authored whole caller/wire/result/JSON/SSE resources. The API input Bot selector and returned underlying model are deliberately different, with several model_usage endpoints, original group-chat/user/plugin metadata, reverse tool outputs, references and exact unknown values. Actual DONE is exposed as original data with no invented JSON, and per-choice finish is not global completion. A Responses-named business event does not become a Chat/Bot native failure.

The fixed client performs one HTTP call without model Session/cache/admin/runtime or URL fetching. Tests cover immutable requests before factory, reserved root ownership, original input/credential policies, raw native errors and normal EOF/length/byte/tail/late-error/async/sync/client/target/final-cancellation boundaries before terminal/complete-result delivery. No live published app/model/plugin/setting has been queried.
