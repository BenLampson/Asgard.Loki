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

These suites use no live credentials, network or Redis. Passing them establishes the tested conversion contracts, not complete losslessness or upstream model acceptance. No Claude, Gemini or converted live request is authorized by ordinary tests. The current live environment-variable contract remains the four Volcengine variables above.

The conversion suite includes adversarial regression fixtures for known-but-unconsumed nested fields, malformed usage, repeated/orphan tool results, sequential same-name Gemini calls and generated/explicit ID collisions. Full valid Responses transcripts exercise every done event; corrupted copies check item IDs, indices, sequence numbers, text/argument snapshots, closed-block mutation and duplicate terminal rejection. Claude lifecycle omissions, pending-tool interleaving, incomplete item status and usage-only tails have separate assertions. Native Gemini tests also reject duplicate finishes and data after error/block terminals while preserving a valid usage-only tail. The current offline totals are 313 conversion tests, 22 Claude tests and 36 Gemini tests, with 672 tests across the solution.

Optional WebSocket core tests use an in-memory duplex adapter and the existing MemoryDistributedCache/coordinator. They cover complete-target selection, input snapshots, Session loss/replacement, changed provider binding identity, HTTP/WebSocket response ownership, cross-session steering/cancellation rejection, native field preservation, concurrent writes/readers, cancellation and disposal. No socket or live provider is opened; native OpenAI transport/framing/terminal tests remain a separate provider checkpoint.

When a local sandbox prevents Microsoft.Testing.Platform's named-pipe connection, run the built xUnit executable directly, for example `dotnet tests/Asgard.Loki.LLM.Conversion.Tests/bin/Release/net10.0/Asgard.Loki.LLM.Conversion.Tests.dll -noLogo -parallelMode none`. CI continues using the ordinary `eng/Test.ps1` discovery and Windows/Linux matrix.

## Native Responses compaction (offline)

OpenAI's compaction tests use the real provider and public SDK with substitute HTTP, `MemoryDistributedCache` and explicit test coordination. They cover model/address/authentication mapping, complete text/item input and native previous-response fields, zero/false/null extensions, full encrypted/retained-window replay, cancellation, HTTP errors without retries and malformed result rejection. Session tests exercise feature-filtered first binding, an incapable existing target, different selectors after reconnect, foreign/missing response ownership and a deterministic stale-read race where the winning target lacks the requested feature. A compaction ID is never treated as a generated-response continuation ID. Core tests verify unsupported feature registration, legacy custom client compatibility and unchanged default target serialization. Conversion regressions reject opaque compaction in requests/results/events even under explicit loss consent.

These are protocol tests only. No live OpenAI/Ark compaction credentials, account/model validation or remote Redis coordination are part of this suite.

## Local setup and future expansion

Use [.env.example](../.env.example) as a naming checklist. It contains only the four names and no credentials. A local `.env` is ignored by Git but is not loaded automatically. Prefer process or current-user environment variables; never execute a dotenv file as shell code or dump the complete environment for diagnostics.

Future live provider fixtures must be separate and explicitly invoked. Use uppercase ASCII names with the `ASGARD_LOKI_TEST_` prefix and keep different capability credentials independent. Keep generation parameter matrices in test data. Check observable constraints and meaningful combinations; statistical or perceptual effects require repeated comparison or manual evaluation rather than a fixed generated sentence.

Redis integration, audio, images and OpenAI live verification remain outside this current fixture. Future Redis tests must explicitly provide a dedicated test cache and shared coordinator, isolate keys per run and never flush the database. No real-provider credentials are supplied to ordinary CI or fork PRs. Running a live test does not authorize NuGet publication.
