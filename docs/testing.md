# Live LLM test configuration

This is a test-fixture contract, not a production configuration API. Library consumers continue to supply typed options and dependencies explicitly. At present only Volcengine LLM has configured live credentials; other capabilities and providers are not selected or probed.

## Four environment variables

Use exactly these four variables for the current live suite:

| Variable | Requirement |
| --- | --- |
| `ASGARD_LOKI_TEST_LIVE` | Defaults to `false`. Only `true` enables live calls. Accept `true`/`false` case-insensitively; reject other non-empty values. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_API_KEY` | A complete, valid standard Ark API Key; never commit or log its value. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_CHAT_MODEL` | Actual upstream Chat model or inference endpoint ID. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_RESPONSES_MODEL` | Actual upstream Responses model or inference endpoint ID. |

Both model variables can contain `doubao-seed-evolving`; they remain independent so a future tester can choose different authorized models. Credentials alone must not enable network calls. Do not implicitly read generic variables such as `OPENAI_API_KEY`, or credentials from another project.

The current fixture fixes its standard Ark API root to `https://ark.cn-beijing.volces.com/api/v3/`. This is the standard API, not the Coding Plan endpoint. `LiveLlmContext` accepts an explicit address override in test code, and production callers can configure `LlmTarget.BaseAddress`. There is no additional address environment variable. See the [official address/authentication documentation](https://docs.volcengine.com/docs/ark/base-url-and-authentication?lang=en).

Prompts, parameter cases, public aliases, Session IDs, request limits and timeouts belong in test code. There are no automatic retries. Each complete call, including stream consumption, has a 90-second deadline. Smoke caps output at 128 tokens; parameter cases normally use 512, reasoning uses 1024, and limit cases use one. Each scenario context permits at most five requests; a process-wide atomic budget rejects requests beyond 32 before sending. These values are deliberately not environment variables. Add credentials and environment contracts for other providers/capabilities only when they are actually introduced.

## Offline and live execution

Ordinary `eng/Test.ps1` and automatic CI remain offline even when live variables are present. They use substitute HTTP handlers and `MemoryDistributedCache` and do not consume provider credentials.

The live project is `tests/Asgard.Loki.LLM.Volcengine.IntegrationTests`. It is excluded from `Asgard.Loki.slnx` and does not match the ordinary `*.Tests.csproj` discovery. It uses xUnit v3 and actual HTTPS requests through the public Loki client. It still uses `MemoryDistributedCache` and a process-local test coordinator; this does not verify Redis or cross-process locking.

Run explicitly from the repository root:

```powershell
pwsh -File eng/Test-Live.ps1
pwsh -File eng/Test-Live.ps1 -Suite Parameters
pwsh -File eng/Test-Live.ps1 -Suite All
pwsh -File eng/Test-Live.ps1 -Suite Parameters -Scenario Structured
```

The script reads these four process environment variables. On Windows it fills missing values from current-user environment variables, which also supports terminals started before user settings were changed. Existing process values take precedence, including an explicit `ASGARD_LOKI_TEST_LIVE=false`. It does not read or execute `.env` files.

With live testing disabled, xUnit reports selected cases as skipped. With it enabled, all three required target settings are validated before any network request; a missing setting fails and reports only the variable name. HTTP or protocol failures fail the test and must never be described as successful connectivity. The script builds with warnings as errors and runs cases sequentially using the native xUnit runner.

| Suite | Cases | Planned maximum requests |
| --- | --- | --- |
| `Smoke` (default) | 1 | 5 |
| `Parameters` | 21 | 26 |
| `All` | 22 | 31 |

`-Scenario` selects one parameter group and requires `-Suite Parameters` or `-Suite All`. Groups are `Structured` (4 requests), `Tools` (8), `ToolChoice` (2), `Reasoning` (8), `Limits` (2), and `Replay` (2). Selecting a group runs only that group. Repeated manual invocations each get a new process budget; opt-in does not impose an account-wide spending cap.

The [parameter verification record](llm-parameter-verification.md) documents native protocol shapes, test evidence and limitations. Current `doubao-seed-evolving` behavior fails both strict `tool_choice=none` cases despite correct final payloads. Accordingly `ToolChoice`, `Parameters` and `All` exit nonzero while that issue remains; the assertions are deliberately retained.

## Offline custom-tool conversion

The opt-in free-text custom complete-call slice adds 64 cases: independently authored full Chat/Claude/Gemini request/response goldens, exact arbitrary-text and result replay, normal/custom same-name separation, public SDK final HTTP bodies, concurrent out-of-order completion, original metadata retention, wrong-context and reused-ID failures, UTF-8/table/history bounds, option binding and unsupported semantic rejection. That first checkpoint rejected custom streaming; the subsequent stream checkpoint implements it with 59 additional cases. No tool code is executed and no live custom-tool model call was made. Native grammar/unknown fields remain preserved; grammar/program/async conversion cannot be enabled with loss consent; namespace identity has its separate opt-in below.

Custom stream fixtures independently author complete native sequences and all 17 target events, including sequence/index/item identities, original text, ordinary/custom separation, done fields and final snapshots. Fifteen public SDK cases exercise fragmented SSE, native metadata, whole completed message/tool history replay, explicit length termination, truncation, bad wrappers and unknown names without retries. Focused cases cover exact UTF-8 limits, pre-terminal raw buffer limits, combined returned-call limits, split escaped surrogate pairs, immutable/native contexts, wrong context, reused/duplicate IDs, interleaving and errors. A failed source emits no successful terminal; explicit incomplete finishes remain incomplete. Existing ordinary/native stream regression suites remain active.

Namespace identity adds 90 cases. Independent full request/response and 17-event fixtures cover root prefix lookalikes, same-name tools in different namespaces and custom/function kind separation. Twenty-eight public SDK cases cover final HTTP/SSE, whole history replay, namespace-only functions with custom disabled, description rejection/consent metadata, concurrent response isolation and truncation without retries. Focused tests verify optional function-result name/namespace by call ID, wrong contexts, invalid scope values, tuple/separator collisions, unchanged Schema constraints, bounded empty groups, historical-only tools, global choices without deleting tools, unsupported selectors/unknown fields and binding-option changes. Nonempty group guidance requires an explicit diagnostic; no namespace/function selector syntax is invented. These remain offline tests; no tool code or hosted search is executed.

## Offline hosted-search configuration

Hosted search request configuration adds 94 cases. Six independently authored target request goldens and 18 public SDK HTTP/SSE cases verify native tool types, fixed-target declarations, legal text-only replies, pre-HTTP rejection and encrypted reply failures without retries. The text replies intentionally contain no successful search result: this is wiring evidence, not live search or complete citation-conversion evidence. Focused tests cover bare domain/location transfer, context-size consent, explicit true/false permissions, count/range/trigger restrictions, duplicate tools, independent function-combination declarations, namespace/custom coexistence, direct/text-only variants and binding changes. Known URL citations, search execution/errors and generateContent grounding have specific loss diagnostics. Encrypted Claude result/reference state hard-fails in complete, history and stream conversion even with ordinary loss consent, while native copies and ordinary user JSON remain intact. Full result/citation mapping remains pending.

## Offline complete URL citations

Complete Chat/Responses URL citations add 64 cases, including two independently authored complete target goldens and four public SDK HTTP/SSE cases. Assertions cover unchanged Unicode text/indices, exact URL/title/order, duplicate sources, safe ASCII-prefix rebasing, diagnosed unknown Unicode units, malformed/missing ranges, unknown fields, function association and original JSON. No exact Unicode index-unit guarantee is inferred from the protocol's character terminology; the byte-length range bound is only a plausibility check. Complete conversion does not imply history/stream support. A separate nine-event native Responses transcript verifies raw copies, strict failure before success and explicitly consented stream loss without a new done/final consistency regression. No live model/search request is made.

## Offline PDF input conversion

User PDF inputs add 126 cases, including twelve directions with independently authored complete request goldens/history roundtrips and 42 public SDK HTTP/SSE cases. Byte assertions include NUL/non-UTF-8 values; separate tests cover multiple files/order, filename preservation/consent, null/missing data, invalid Base64, MIME and role boundaries, conflicting sources, unknown nested controls and native reference copies. Unresolved/private references fail before HTTP, including with ordinary loss consent. The direct Claude URL-PDF to Responses case sends one model request and no SDK media fetch. Sources/targets and all reply streams use their own native protocol fixtures.

Media samples are opaque protocol bytes labelled PDF, not renderable documents. Substitute ordinary-text replies verify SDK transport and conversions, not PDF acceptance, extraction quality or live-model behavior. No credentials, Files API, network download, PDF parser or file resolver is used. Non-PDF input, generated files and rich tool-result conversion remain separate checkpoints.

## Offline rich tool-result conversion

Rich results add 120 cases: eight authored full request/roundtrip comparisons, string/array and empty-block distinctions, media/PDF preservation, call-ID association, namespace/custom histories and exact custom-array UTF-8 JSON bounds. Forty public SDK HTTP/SSE cases verify complete final native requests, ordinary substitute replies, original upstream metadata and failures before any HTTP. Custom Unicode is checked before JSON serialization; invalid surrogate text is not accepted as replacement data. No tool is executed.

Regressions cover explicit error flags (hard native-only for true), invalid/null flags, unknown fields, private image/file references, malformed Base64, nested encrypted citations, ordinary user JSON, duplicate/orphan IDs, parallel same-name reverse-order results and Chat turn order. Supported strings retain their original kind; unsupported Chat media/Gemini arrays fail even with ordinary loss consent. The prior custom empty-array rejection sample is replaced with an actually unsupported future content kind, with new positive empty-array and byte-boundary cases. Media fixtures remain opaque protocol bytes and normal text replies; they do not establish supplier acceptance or live tool/media behavior.

## Five-request smoke scenario

One integration test performs five requests in order:

1. Chat Completions JSON mode: assert a nonempty response ID, positive token usage, and an actual JSON object with the requested field/value.
2. Chat Completions streaming: consume the full stream, accumulate actual text and require a normal finish reason and protocol termination.
3. Responses creation: store a synthetic random marker in upstream context, require completed status, positive usage and actual output text containing the marker.
4. Responses streaming: accumulate text deltas, require a successful final event, and compare streamed text with the final response.
5. Responses continuation: build a new LokiClient sharing the test cache, send only the previous response ID and a question, and verify the original marker and target binding survive. No previous message history or marker is resubmitted.

A delegating HTTP handler validates authentication without displaying credentials, captures the final request payload, enforces the request budget, and passes the request to a real HTTP handler. Assertions check actual model mapping, JSON mode, temperature transmission, thinking configuration, stream switches, store and previous_response_id. A temperature field being sent is not evidence of its statistical effect.

The fixture saves a sanitized report under ignored `artifacts/live/volcengine-llm/`: pass/fail, completed scenarios, model names, response IDs, paths, HTTP status codes and bounded upstream error codes. It does not save credentials, authorization headers or full response bodies. A report can contain partial evidence from a failed attempt; require `passed: true` before reporting the full scenario as successful.

## Native Claude/Gemini and explicit conversion (offline)

`Asgard.Loki.LLM.Anthropic.Tests` and `Asgard.Loki.LLM.Google.Tests` use the real provider implementations through public SDK clients with substitute HTTP handlers, fragmented UTF-8 SSE, `MemoryDistributedCache` and test-only coordination. They check native message/part preservation (including signatures and encrypted/future data), final URI/authentication/model mapping, caller immutability, zero/false/null/empty values, reserved-field rejection, HTTP errors, cancellation, Session continuation and missing terminal events.

`Asgard.Loki.LLM.Conversion.Tests` owns hand-maintained native request, response and stream fixtures for Chat Completions, Responses, Claude Messages and Gemini. All twelve directions have independent assertions on destination field paths, tool definitions/call IDs/results, inline images, output limits and base usage. Streaming assertions check actual text before completion, complete tool arguments, final payloads, item indices, event sequencing and termination. Twenty-four public SDK pipeline cases use real native HTTP adapters to cover both modes in every direction. Additional cases cover diagnostics, native identity copies, explicit loss policy, schema/effort mapping, orphan tools, truncation and invalidation of changed conversion configuration.

An additional 36 acceptance cases compare complete requests, responses and event transcripts against independently authored embedded JSON golden fixtures. Tests never auto-update snapshots and do not normalize away IDs, arguments or terminal ordering. Representative response envelopes include metadata, executed-request echoes, optional nulls and all done events. Another 24 public SDK/HTTP cases exercise reasoning and structured output settings in both complete/stream modes for every direction. Separate tests cover budget/mode/output intent, unchanged Schema constraints, explicit metadata/enforcement loss, parallel booleans and binding invalidation when any new option changes.

The modern Gemini `responseFormat.text` extension adds six independently authored complete-request snapshots and twelve HTTP/SSE pipeline cases across its six relevant directions. Tests cover explicit versus legacy target selection, JSON/plain text modes, nested unknown-field diagnostics, incompatible MIME/Schema combinations, mixed modern/legacy text rejection and binding invalidation. Native/same-protocol copies stay unchanged. These offline cases establish wire mappings, not actual model acceptance.

The typed Gemini `responseSchema` source checkpoint adds 59 cases: three independent complete-request snapshots, six public SDK HTTP/SSE cases, a pre-HTTP hard-rejection case and focused constraints/type/numeric/order regressions. Tests preserve nullable enums, nested anyOf/items/properties, zero/false/null annotations, exact int64 exponent forms and property insertion order. They reject alias conflicts, invalid/overflow/fractional limits and unknown typed validation constraints even with `AllowLoss`. ProtoJSON ordinary null/empty repeated fields retain unset semantics. Typed target output remains separate work. No actual model acceptance is claimed.

The typed function `parameters` checkpoint adds eighteen cases: three independently authored full-request snapshots with reverse history/schema replay, six public HTTP/SSE final-wire cases, conflicting dialects, ordering diagnostics, unknown-constraint hard failures and native-copy/pre-HTTP rejection checks. Call IDs, argument JSON, results and named tool choice are compared with the complete hand-authored expectations.

Explicit typed output-schema targets add 51 cases, including three complete independently authored request snapshots with Schema replay, six public HTTP/SSE cases and two pre-HTTP failure cases. Focused assertions cover nullable types, nested property-order guarantees, exact int64 strings, decimal-roundtrip double validation, unavailable keywords/enums/unions, explicit open objects, example annotation-loss consent, plain modes, default/native-copy invariants and changed target-option Session invalidation. These tests establish the typed output-schema subset, not arbitrary JSON Schema support or typed target function parameters.

Explicit typed function targets add 24 cases: three reverse-direction full-request snapshots with complete history replay, six public HTTP/SSE final-wire cases, two pre-HTTP failures, all six text/function dialect combinations, unavailable constraints/default retention, unknown option values, native-copy behavior and Session invalidation. They reuse the independent hand-authored `typed-function.json` expectations and the reviewed numeric/constraint writer, preserving call/result identity without adding a function property-order guarantee. This does not execute tools or establish live-model acceptance.

Caller-declared target settings add 113 cases. Forty-eight public SDK/HTTP cases exercise allowed/denied effort in all twelve directions and both modes; a native-branch case proves these are not global request filters. Focused cases cover final budget/effort replacements, caller defaults, absent/null/zero/false fields, manual/adaptive/disabled modes, bounds, every output envelope and multiple diagnostics. Configuration tests check eager invalid-profile rejection, independent collection snapshots, equivalent-set identity and changed-profile Session invalidation. Response usage stays unchanged and no upstream entitlement/model acceptance is claimed.

These suites use no live credentials, network or Redis. Passing them establishes the tested conversion contracts, not complete losslessness or upstream model acceptance. No Claude, Gemini or converted live request is authorized by ordinary tests. The current live environment-variable contract remains the four Volcengine variables above.

The conversion suite includes adversarial regression fixtures for known-but-unconsumed nested fields, malformed usage, repeated/orphan tool results, sequential same-name Gemini calls and generated/explicit ID collisions. Full valid Responses transcripts exercise every done event; corrupted copies check item IDs, indices, sequence numbers, text/argument snapshots, closed-block mutation and duplicate terminal rejection. Claude lifecycle omissions, pending-tool interleaving, incomplete item status and usage-only tails have separate assertions. Native Gemini tests also reject duplicate finishes and data after error/block terminals while preserving a valid usage-only tail. The current offline totals are 1238 conversion tests, 22 Claude tests and 36 Gemini tests, with 1671 tests across the solution.

Optional WebSocket core tests use an in-memory duplex adapter and the existing MemoryDistributedCache/coordinator. They cover complete-target selection, input snapshots, Session loss/replacement, changed provider binding identity, HTTP/WebSocket response ownership, cross-session steering/cancellation rejection, native field preservation, concurrent writes/readers, cancellation, disposal and close/dispose while cache validation is awaiting. No socket or live provider is opened; OpenAI provider framing tests are described below and lifecycle integration remains a separate checkpoint.

The OpenAI provider uses an in-memory `WebSocket` substitute. Framing cases verify fragmented Unicode, strict UTF-8, malformed/binary/partial messages, accumulated byte limits, complete-message send flags, queue snapshots, single-reader/serialized-writer behavior, active versus queued cancellation, close interruption and socket disposal. Public SDK cases separately verify handshake URL/authentication, complete-target and response ownership, parallel lanes/FIFO queues, lifecycle ID/status/sequence corruption, pending-request closure, error/control correlation (including late completed-control errors, ID reuse, missing correlation IDs, lane isolation and unsent-control rollback), unknown future events, bounded state and factory-identity changes. Steering cases cover unallocated failures without IDs, multiple accepted inputs committing to one successor, accepted/pending disconnects and explicit caller tool-result continuation without any SDK execution. No network or live key is used.

When a local sandbox prevents Microsoft.Testing.Platform's named-pipe connection, run the built xUnit executable directly, for example `dotnet tests/Asgard.Loki.LLM.Conversion.Tests/bin/Release/net10.0/Asgard.Loki.LLM.Conversion.Tests.dll -noLogo -parallelMode none`. CI continues using the ordinary `eng/Test.ps1` discovery and Windows/Linux matrix.

## Native Responses compaction (offline)

OpenAI's compaction tests use the real provider and public SDK with substitute HTTP, `MemoryDistributedCache` and explicit test coordination. They cover model/address/authentication mapping, complete text/item input and native previous-response fields, zero/false/null extensions, full encrypted/retained-window replay, cancellation, HTTP errors without retries and malformed result rejection. Session tests exercise feature-filtered first binding, an incapable existing target, different selectors after reconnect, foreign/missing response ownership and a deterministic stale-read race where the winning target lacks the requested feature. A compaction ID is never treated as a generated-response continuation ID. Core tests verify unsupported feature registration, legacy custom client compatibility and unchanged default target serialization. Conversion regressions reject opaque compaction in requests/results/events even under explicit loss consent.

These are protocol tests only. No live OpenAI/Ark compaction credentials, account/model validation or remote Redis coordination are part of this suite.

## Native Bedrock ConverseStream (offline)

The AWS supplier has 300 offline cases, including 71 for native ConverseStream. An independently encoded 27-frame binary fixture, complete native event/typed-header/payload manifests and four independently calculated signature vectors cover eight direct/public HTTP paths. The native stream keeps tool/result IDs, exact indices/numbers, reasoning/signatures/redacted data, image/citation deltas and unknown fields/events. Converse transport names determine event Type; unknown payload type/error/bytes fields do not inherit Claude semantics.

CRC-valid lifecycle negatives verify implicit text/reasoning starts, explicit tool/result/image starts, duplicate/missing/reopened associations and required messageStop/metadata. Late transport/service/CRC/length/read/disposal/cancellation failures cannot deliver held success. Source-await snapshots, exact preceding default binding bytes, explicit policy invalidation and JSON/stream credential rotation/Session recovery are separate regressions. Shared binary-reader CRC tests remain in the Claude EventStream suite. All tests use substitute HTTP, deterministic source/time and MemoryDistributedCache with explicit coordination; no AWS account, real key, network, Linux execution or new GitHub Actions result is claimed. The separate core event-type regression brings LLM core coverage to 179 and total ordinary offline coverage to 5435 at that checkpoint.

## Reverse Responses → Gemini citations (offline)

Fifty-four additional cases bring conversion coverage to 1852 and all ordinary offline tests to 5489 at that checkpoint. Full native Responses and five-event Gemini fixtures cover hand-calculated scalar/UTF-16/UTF-8 indices, original/duplicate source order, empty ranges and cumulative text without searching/normalizing. Source snapshots/indices, layout boundaries and independent input/output resource caps are tested with and without loss consent. Fifteen public SDK HTTP/SSE cases verify native body/path/auth, source EOF/disposal before grounding, incomplete finishes, missing configuration, early exit/cancellation and late native/transport faults. An actual preceding v24 converting Session fails before the next HTTP under conversion-v25. Native supplier bindings, 28-package graph and four-variable live contract are unchanged; no live model/search, Linux execution or remote CI result is claimed.

## Declared text-part concatenation (offline)

Forty-seven additional offline cases bring conversion coverage to 1899 and ordinary coverage to 5536, still 28 packages. Three 26-event source fixtures and eight target events independently specify two native messages/four parts, exact scalar/UTF-16/UTF-8 units, non-ASCII/empty prefixes, duplicate references and complete request/wire JSON. Part-key/native-order checks are tested even with equal texts; early empty starts do not falsely count as content interleaving. Twenty-two public SDK cases cover full HTTP/SSE, late transport/source errors, cancellation/early exit, partial status, wrong-direction pre-HTTP rejection and mode-dependent Session affinity. Default/single-profile v25 binding bytes stay exactly compatible; input/output limits and hard layouts cannot be bypassed by AllowLoss. No real provider/search, Linux or new GitHub Actions result is claimed.

## Bedrock Runtime API Key authentication (offline)

Eighty-one cases add an explicit native Key/Bearer constructor while retaining all 300 preceding AWS regressions. Eight authored HTTP manifests plus complete native JSON/binary/event/header/payload goldens validate sixteen public/direct actual-model/ARN/operation combinations without a real credential. Additional cases cover default-header isolation, selected Host/opaque Key, pre-factory snapshots, secret-free credentials/cache, key/config/auth-mode binding changes, cross-client JSON/stream target recovery, all four methods' late read/disposal/cancellation, original errors and early exit. Three common-text converter cases prove auth delegation and late-failure finish rejection. Current ordinary totals are 5617 (381 AWS), 28 packages. Existing SigV4 identity/signatures and core/conversion-v25 stay unchanged; no AWS login, live Key/model, Linux or remote CI result is claimed. The four-variable live contract remains unchanged.

## Local setup and future expansion

Use [.env.example](../.env.example) as a naming checklist. It contains only the four names and no credentials. A local `.env` is ignored by Git but is not loaded automatically. Prefer process or current-user environment variables; never execute a dotenv file as shell code or dump the complete environment for diagnostics.

Future live provider fixtures must be separate and explicitly invoked. Use uppercase ASCII names with the `ASGARD_LOKI_TEST_` prefix and keep different capability credentials independent. Keep generation parameter matrices in test data. Check observable constraints and meaningful combinations; statistical or perceptual effects require repeated comparison or manual evaluation rather than a fixed generated sentence.

Redis integration, audio, images and OpenAI live verification remain outside this current fixture. Future Redis tests must explicitly provide a dedicated test cache and shared coordinator, isolate keys per run and never flush the database. No real-provider credentials are supplied to ordinary CI or fork PRs. Running a live test does not authorize NuGet publication.

## OpenAI HTTP EOF and native snapshot checkpoint

At the HTTP checkpoint, the ordinary inventory was 5681 passing offline tests across 27 test projects (the reserved Video project is empty), including 275 OpenAI LLM cases; production packaging remains 28 packages. Sixty-four new tests cover independently authored complete Chat/Responses native request/response/SSE manifests through the public SDK, raw field/frame metadata fidelity, strict encoding/JSON/length limits, terminal-tail limits, DONE continuation to EOF, late native errors, read/disposal/final cancellation, early exit and full-target cross-instance Session recovery. Native non-success terminals stay native. The HTTP suite does not claim full per-choice/tool accumulation, endpoint/model acceptance or real authentication.

Local Release build with warnings as errors, all ordinary tests, pack and dependency/metadata verification are the checkpoint gates. They do not invoke GitHub Actions, restore live credentials, run Redis or certify Linux. Existing four-variable Volcengine live configuration is unchanged. See [HTTP policy and Session migration](../src/Asgard.Loki.LLM.OpenAI/README.md#http-resource-policy-and-terminal-fidelity).

## Responses ownership regression checkpoint

Nine public SDK cases exercise known lifecycle and unknown extension events through actual OpenAI SSE, `MemoryDistributedCache` and the explicit test coordinator. Native lifecycle IDs register before event delivery. Unknown JSON type and SSE event-name forms preserve complete business objects without response ownership; empty IDs cannot break the stream, and a continuation using a foreign business ID fails before any second HTTP request. Current ordinary totals are 5690 (284 OpenAI), 27 test projects and 28 production packages. No previous-cache purge, live request, remote Actions run or Linux result is claimed.

## Anthropic native Messages HTTP checkpoint

Current totals are **5794 offline tests** across 27 ordinary test projects (reserved Video remains empty), including **126 Anthropic cases**, and 28 production packages. The checkpoint adds 104 cases: three complete hand-authored standard/Runtime/Mantle native manifests and 24 direct/public HTTP/JSON/SSE snapshots; auth/header isolation, strict byte/JSON/length limits, source block/tool lifecycle, state limits, pre-factory validation/snapshots, actual predecessor Session rejection and cross-client binding tests; late read, async-only and sync disposal, client disposal/final cancellation and native error handling.

The old shared Claude minimal stream fixture now has a real message_start, stop-reason delta and message_stop instead of a lone stop event. Gemini's branch is unchanged. No test uses real Keys, cloud login, model execution, network or Redis. Tests prove explicit wire behavior, not cloud/model acceptance of Bearer, reasoning, schemas or media. Volcengine live variables and entry remain unchanged. The local Release/ordinary-tests/28-package boundary and metadata gates are independent of GitHub Actions; no Linux/remote CI claim follows from them.

## Fixed Messages conversion/settings follow-up

Current totals are **5811 offline tests**, including **1915 conversion** and **127 Anthropic**, 27 ordinary test projects and 28 production packages. Sixteen conversion cases use a complete independently authored source/target/native response/SSE fixture through public SDK HTTP. Assertions cover high/off thinking, unchanged explicit max output, exact Schema constraints/large integers, selected auth, the single schema-name loss diagnostic and required consent, off/budget conflict, original late errors and absence of target success. Complete native output remains available as upstream metadata.

The full native Messages fixture audit corrected array/index consistency; one additional native raw-sparse association test and two of the conversion cases explicitly separate passthrough from valid complete content-array conversion. No strict source check was weakened to pass an invalid fixture. Production conversion identity remains v25, no public API or dependency changes, and no real cloud, Linux, remote Actions or live variable changes are included.

## Mantle SigV4 Messages checkpoint

Current ordinary inventory is **5918 passing offline tests** across 27 ordinary test projects (reserved Video remains empty), including **488 AWS cases**, and 28 production packages. Mantle adds 107 cases and four independently authored native resources. Sixteen Python SHA256/HMAC vectors assert complete final HTTP bytes/signatures for native/public, JSON/SSE, permanent/temporary credentials and ordinary/encoded proxy roots with a port. Existing Runtime vectors remain unchanged.

New tests exercise original fields/frame metadata and exact numbers, explicit `bedrock-mantle` scope, selected/unselected defaults, source/config/principal/expiry/cancel checks, before-wait snapshots, Key/Token rotation with full-target recovery, invalid options/roots, strict raw bytes/UTF-8/JSON/length and state limits, native lifecycle association, original/late errors and read/async-only/sync/client disposal before terminal publication. Three public converter cases verify signed External identity delegation and no converted finish after late native errors. Every HTTP request uses a substitute handler; credentials are public AWS examples, caches use memory/test coordination, and no actual upstream request, AWS login, Redis, Linux, remote Actions or live-variable change is included.

## Nova complete JSON checkpoint

Current ordinary totals are **5983 passing offline tests** across 27 test projects (reserved Video remains empty), including **539 AWS**, **191 core** and **1917 conversion**, with 28 production packages. The checkpoint adds 51 AWS, 12 core and two conversion-boundary cases. Independent native v1/v2 full requests/results and sixteen Python final-byte/HMAC vectors cover model/ARN paths, exact raw bodies, direct/public calls and temporary credentials, plus explicit API Key/Bearer cases. Raw reasoning/media/tools/results, stop/usage/metrics, future fields and large integers remain intact.

Direct Nova tests check snapshot-before-source-await, strict byte/JSON/UTF-8/media/length, original root/HTTP failures, EOF/async-only/sync/client disposal and final cancellation, protocol isolation and full-target recovery. Core fake adapters verify complete/stream abstractions and pre-Session snapshots independently of AWS. **AWS Nova binary streams are not implemented at this checkpoint** and fail before credentials/HTTP; no fake model or real stream verification is claimed. Two new converter tests keep Nova outside the existing matrix even with loss consent or same-protocol copies. All ordinary tests are offline, with no live variables, AWS login, Redis, Linux or remote Actions execution.

## Nova binary stream checkpoint

Current totals are **6069 passing offline tests** across 27 ordinary test projects (reserved Video remains empty), including **625 AWS**, with 28 production packages. 86 additional cases use an independently authored 17-frame Python struct/zlib binary fixture, complete header/payload/envelope manifests and sixteen independent Python HTTP/HMAC vectors. Native v1/v2 direct/public, model/ARN, temporary and explicit Bearer streams compare all final request bytes, frames and opaque/unknown original fields. Native event Type and original outer name stay distinct.

Regressions check required usage/optional metrics; tool/block IDs, sparse raw indices, known union/shape/closure and opaque future reasoning data; body/frame/header/tail/block policies; malformed Base64/inner/outer JSON, CRC/truncation and native service errors; original valid-frame exception views and immutable bytes; EOF/async-only/sync/client disposal, cancellation, early exit and held terminal/tails. Default option-shell identity bytes are verified, explicit Nova policy changes reject prior Sessions before acquisition, and rotated credentials/full-target recovery support JSON then native stream. The two predecessor unsupported-stream tests now check pre-cancellation without acquisition/HTTP. No live account, new environment variable, Redis, Linux or remote Actions result is included.

## Typed source stream error checkpoint

Current local totals are **6125 passing offline tests** across 27 ordinary test projects (reserved Video remains empty), including **1973 conversion**, with 28 production packages. Fifty-six new conversion cases use independently authored full Claude/Responses source streams and a full Chat golden. Extension losses remain diagnosed, actual native error types and untyped errors remain failures, and original tool IDs/arguments/usage/exact integers are preserved. Eighteen public SDK/fragmented-SSE cases cover EOF/disposal before terminal delivery and late native/read/dispose/cancellation failure. Tests use substitute HTTP, MemoryDistributedCache and explicit test coordination; no network, real credentials, Redis, live model, Linux run or GitHub Actions result is claimed. No new public interface, protocol mapping, binding option or package dependency is introduced.
