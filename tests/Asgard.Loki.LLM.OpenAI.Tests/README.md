# Asgard.Loki.LLM.OpenAI.Tests

**752 passing offline xUnit v3 cases** exercise actual SDK/provider JSON, SSE and WebSocket calls. HTTP, sockets, external token sources and clocks are substitutes. Session tests use MemoryDistributedCache plus the explicit test coordinator; no real Key, auth-file/environment lookup, network, Redis or remote CI is required. See [testing](../../docs/testing.md).

| Contract | Verification |
| --- | --- |
| Chat/Responses HTTP and native compaction | Independent complete request/result/raw SSE windows; original tools/reasoning/encrypted/schema/unknown values and transport identity; full target routing and native response ownership |
| Responses WebSocket / Realtime | Independent complete client/server transcript; native events/tool/audio/status/error data, bounded fragmentation, snapshots, cancellation and owned socket lifetime |
| Moderations | 91 cases covering scalar/text-batch/multimodal/native labels/scores/result association, original raw errors and limits; no local content policy |
| Codex Responses/compact and fixed alpha/search | 127 cases over external principal/account/token sources, native windows, opaque search bodies, original errors and complete binding/expiry/closure gates |
| Cloudflare compatible native targets | 14 full account-root/profile cases; original Chat JSON/SSE and nonstreaming Responses; no ai/run conversion |
| Ollama compatible targets | 28 native Chat/Responses/compact profile cases; explicit local placeholder and independent hosted Key; stateless full input, no hosted-compact entitlement claim |
| Native text/FIM | 140 cases over the existing public TextCompletions client and explicit adapter, complete JSON/raw SSE/expected frames, text/logprobs/sparse indices and independent choices/DONE |
| DashScope compatible native targets | 35 whole Chat/Responses/Text profile and fault cases, explicit legacy/workspace/proxy roots, native headers, tools/opaque/schema/reasoning/extensions and complete Session affinity |

Fixtures are independently authored native contracts, never snapshots generated from SDK output. Whole-body and per-event comparisons retain original tool/item/call associations, reversed history results, reasoning/signatures/encrypted values, schema settings, unknown fields, precise numbers and explicit null/false/zero/empty values. Returned model data remains distinct from public/actual target metadata. Native passthrough does not aggregate tool arguments; strict conversion association belongs to the separate conversion suite.

Boundary matrices cover original UTF-8/Unicode/duplicates/reserved ownership before dispatch, unsupported credential modes, raw HTTP200/native/binary errors, byte/declared-length/media/framing policies, exact selected wire and real EOF. Terminal/usage/unknown tails wait through async/sync/client/request disposal and final cancellation. Early exit releases only owned call resources. Native text additionally bounds distinct indices, requires all observed choice finishes plus actual DONE, and refuses repeated/late text or logprobs. Unknown native data never substitutes a missing terminal.

Responses known reasoning_text/custom_tool_call_input/web_search_call lifecycle cannot reopen after completed. Chat unknown nonstring type/business IDs and response.failed-named business metadata remain raw; actual native errors stay failures. Full independent DashScope windows demonstrate legitimate preterminal events and unchanged optional fields without gateway parameter repair.

Cache/coordinator tests validate whole root/model/key/principal/account/source/implementation/policy/client/version binding, source rotation under a stable principal, native response ownership, concurrent/stale first binding and cross-instance recovery. Moderation/text/compaction/search IDs do not acquire generated Responses ownership. Default nullable policy omission preserves earlier binding bytes; explicit semantic changes reject old Sessions before another send.

These tests verify local protocol fidelity and failure boundaries, not actual model/account availability, optional-setting effectiveness, search/plugin results, generated quality, cross-process Redis coordination, Linux or a full-repository live validation. Generic token-array/logprobs/unknown-field text fixtures do not certify DashScope accepts those inputs. No generated request is retried or retargeted by the SDK.
