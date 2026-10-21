# Testing Asgard.Loki

## Run offline tests

Use .NET 10 and run from the repository root:

```powershell
dotnet restore Asgard.Loki.slnx
dotnet build Asgard.Loki.slnx -c Release --no-restore -warnaserror
pwsh -File eng/Test.ps1
```

For one project after building the matching Release configuration:

```powershell
dotnet test --project tests/Asgard.Loki.LLM.OpenAI.Tests/Asgard.Loki.LLM.OpenAI.Tests.csproj -c Release --no-build
```

Ordinary `*.Tests.csproj` projects use xUnit v3 / Microsoft.Testing.Platform. Each project must discover and execute tests; zero discovered tests is a failure. HTTP, sockets, clocks and external credential sources are test doubles. Session tests use `MemoryDistributedCache` with an explicit in-process coordinator, which does not prove distributed locking. Ordinary tests do not read real credentials, contact suppliers, open a service or require Redis.

## Test ownership

| Layer | Tests belong here |
| --- | --- |
| Entry and Abstractions | Module registration, initialization, isolation and disposal |
| Infrastructure | Shared body/transfer bounds, framing, encoding, cancellation and borrowed-stream ownership; see the [boundary ownership table](../tests/Asgard.Loki.Infrastructure.Tests/README.md#shared-boundary-ownership) |
| Capability core | Public contracts, snapshots, routing, Session/task ownership and application-owned adapter lifecycle |
| Native supplier | Actual path/auth/signature, original request/result fields, native tool/task associations, provider-owned parser and terminal policy, plus necessary integration with shared transport |
| Compatible profile | Full native wire and path/auth configuration, identity binding and a small wiring regression; reuse the native suite's protocol matrices |
| Conversion | Supported mappings, diagnostics, original field/tool/citation association and iterator ownership; supplier byte/framing matrices stay in their own suites |

Tests and their names define the executable cases. The corresponding `src/Asgard.Loki.*` README documents supported operations, initialization and limits. A second coverage inventory is not maintained in every test project.

## Fixture conventions

- Caller inputs, expected final requests, native responses and expected events are independently authored. Do not generate or automatically rewrite expected data with production serializers, converters or signers.
- Compare the complete native structures and original array/event order. Preserve exact numbers, explicit absence/null/false/zero, unknown values, tool IDs and reversed result associations. Normalize only deliberately synthetic values; multipart comparisons may normalize the random boundary.
- Fixture credentials, hosts, media and signatures are synthetic. Arbitrary Base64 or binary media is a transport sample, not evidence of a valid image/audio/video/PDF or of live model acceptance. CRC and HMAC/signature vectors use independently specified bytes and inputs.
- SSE/NDJSON transcripts and expected JSON event lists serve different purposes. Spark JSONL resources store individual WebSocket messages; their line format is not an NDJSON upstream protocol. Native completion, actual transport controls and SDK ownership metadata remain distinct.
- Binary EventStream resources must retain exact bytes. The [AWS fixture guide](../tests/Asgard.Loki.LLM.AWS.Tests/README.md) explains frame/signature manifests and CRC-valid negative examples. Do not apply text or newline conversion to them.

The fixture guides below retain interpretation rules that cannot be inferred from ordinary JSON field names:

| Fixture set | Interpretation guide |
| --- | --- |
| Chat/Responses/Claude/Gemini conversion | [Source and target goldens, citations and index units](../tests/Asgard.Loki.LLM.Conversion.Tests/Fixtures/README.md) |
| Shared Nova/Converse data | [Dialect, binary and ID normalization rules](../tests/Shared/Fixtures/README.md) |
| Compatible audio | [Native byte carriers and profile limits](../tests/Asgard.Loki.Audio.OpenAI.Tests/Fixtures/Compatible/README.md) |
| Dify | [Native Chatbot/Agent/Chatflow event ordering](../tests/Asgard.Loki.LLM.Dify.Tests/Fixtures/README.md) |
| Codex backend | [Opaque backend/search examples](../tests/Asgard.Loki.LLM.OpenAI.Tests/Fixtures/Codex/README.md) |
| Native text completions | [Generic transport samples and supplier restrictions](../tests/Asgard.Loki.LLM.OpenAI.Tests/Fixtures/Text/README.md) |
| Ark | [Native model and published Bot identities](../tests/Asgard.Loki.LLM.Volcengine.Tests/Fixtures/README.md) |

## Live LLM test configuration

This is a test-fixture contract, not a production configuration API. Library consumers continue to supply typed options and dependencies explicitly. At present only Volcengine LLM has configured live credentials; other capabilities and providers are not selected or probed.

## Four environment variables

Use exactly these four variables for the current live suite:

| Variable | Requirement |
| --- | --- |
| `ASGARD_LOKI_TEST_LIVE` | Defaults to `false`. Only `true` enables live calls. Accept `true`/`false` case-insensitively; reject other non-empty values. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_API_KEY` | A complete, valid standard Ark API Key; never commit or log its value. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_CHAT_MODEL` | Actual upstream Chat model or inference endpoint ID. |
| `ASGARD_LOKI_TEST_VOLCENGINE_LLM_RESPONSES_MODEL` | Actual upstream Responses model or inference endpoint ID. |

Both model variables can contain `doubao-seed-evolving`; they remain independent so each protocol can use its own authorized model. Credentials alone must not enable network calls. Do not implicitly read generic variables such as `OPENAI_API_KEY`, or credentials from another project.

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

| Suite | Cases | Maximum requests |
| --- | --- | --- |
| `Smoke` (default) | 1 | 5 |
| `Parameters` | 21 | 26 |
| `All` | 22 | 31 |

`-Scenario` selects one parameter group and requires `-Suite Parameters` or `-Suite All`. Groups are `Structured` (4 requests), `Tools` (8), `ToolChoice` (2), `Reasoning` (8), `Limits` (2), and `Replay` (2). Selecting a group runs only that group. Repeated manual invocations each get a new process budget; opt-in does not impose an account-wide spending cap.

The [parameter verification record](llm-parameter-verification.md) documents native protocol shapes, test evidence and limitations. Current `doubao-seed-evolving` behavior fails both strict `tool_choice=none` cases despite correct final payloads. Accordingly `ToolChoice`, `Parameters` and `All` exit nonzero while that issue remains; the assertions are deliberately retained.

The complete live process has a hard limit of **32 upstream requests**, each scenario context is limited to **five requests**, and each complete call (including full stream consumption) has a **90-second** deadline. Validate all selected scenarios' required settings before any request, report missing names only, never borrow another capability's credentials, and never retry generation automatically. Endpoints, parameter matrices, counts and deadlines are test-code policy; they do not add environment variables. Sanitized results go only to ignored `artifacts/live/`, with successful, skipped and failed outcomes kept distinct. Sending a parameter does not prove its effect.

## Five-request smoke scenario

One integration test performs five requests in order:

1. Chat Completions JSON mode: assert a nonempty response ID, positive token usage, and an actual JSON object with the requested field/value.
2. Chat Completions streaming: consume the full stream, accumulate actual text and require a normal finish reason and protocol termination.
3. Responses creation: store a synthetic random marker in upstream context, require completed status, positive usage and actual output text containing the marker.
4. Responses streaming: accumulate text deltas, require a successful final event, and compare streamed text with the final response.
5. Responses continuation: build a new LokiClient sharing the test cache, send only the previous response ID and a question, and verify the original marker and target binding survive. No previous message history or marker is resubmitted.

A delegating HTTP handler validates authentication without displaying credentials, captures the final request payload, enforces the request budget, and passes the request to a real HTTP handler. Assertions check actual model mapping, JSON mode, temperature transmission, thinking configuration, stream switches, store and previous_response_id. A temperature field being sent is not evidence of its statistical effect.

The fixture saves a sanitized report under ignored `artifacts/live/volcengine-llm/`: pass/fail, completed scenarios, model names, response IDs, paths, HTTP status codes and bounded upstream error codes. It does not save credentials, authorization headers or full response bodies. A report can contain partial evidence from a failed attempt; require `passed: true` before reporting the full scenario as successful.

## Offline verification

Run `dotnet build Asgard.Loki.slnx -c Release` and `pwsh -File eng/Test.ps1`. Every ordinary project must discover and execute tests. The suites use substitute HTTP/WebSocket/stream/signature sources, deterministic clocks, MemoryDistributedCache and explicit coordination. They verify native fields, tool/task identity, unknown data, failure statuses, complete EOF/disposal/cancellation and borrowed ownership. Infrastructure has its own public SSE contract tests; provider tests validate native protocol semantics independently.

Offline protocol/fixture acceptance is separate from actual model output quality, provider account authorization, Redis across processes, media decoding or model-specific Schema/reasoning effects. Each package README states its supported operations and live validation status. Ordinary CI remains offline; its Windows/Linux gate runs only for PRs into main or explicit release reuse.

## Local setup

Use [.env.example](../.env.example) as a naming checklist. It contains only the four names and no credentials. A local `.env` is ignored by Git but is not loaded automatically. Prefer process or current-user environment variables; never execute a dotenv file as shell code or dump the complete environment for diagnostics.

Live provider fixtures must be separate and explicitly invoked. Use uppercase ASCII names with the `ASGARD_LOKI_TEST_` prefix and keep different capability credentials independent. Keep generation parameter matrices in test data. Check observable constraints and meaningful combinations; statistical or perceptual effects require repeated comparison or manual evaluation rather than a fixed generated sentence.

Redis integration, audio, images and OpenAI live verification remain outside this current fixture. Redis integration tests must explicitly provide a dedicated test cache and shared coordinator, isolate keys per run and never flush the database. No real-provider credentials are supplied to ordinary CI or fork PRs. Running a live test does not authorize NuGet publication.

## Offline unit-test runner

Ordinary `.Tests.csproj` projects use xUnit v3 with the Microsoft Testing Platform runner selected by `global.json`. Build first, then run `dotnet test --project <project.csproj> --no-build` (matching the build configuration), or use `eng/Test.ps1` for the already-built ordinary suite. Each project must discover and execute tests; zero tests or a runner handshake failure is a failure, not a successful verification. These tests use local doubles and never enable live calls.

The separate `.IntegrationTests.csproj` projects retain the native xUnit command line used by `eng/Test-Live.ps1`; changing the ordinary runner does not enable, discover or execute live integration tests. No remote CI is dispatched by either local command.
