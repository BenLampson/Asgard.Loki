# Volcengine LLM live integration tests

Explicitly invoked xUnit v3 smoke and parameter suites using the public Loki client and actual standard Ark HTTPS endpoints. Excluded from the ordinary solution and `eng/Test.ps1` discovery.

Set these four variables:

```text
ASGARD_LOKI_TEST_LIVE=true
ASGARD_LOKI_TEST_VOLCENGINE_LLM_API_KEY=<your-complete-standard-Ark-key>
ASGARD_LOKI_TEST_VOLCENGINE_LLM_CHAT_MODEL=doubao-seed-evolving
ASGARD_LOKI_TEST_VOLCENGINE_LLM_RESPONSES_MODEL=doubao-seed-evolving
```

Run from the repository root:

```powershell
pwsh -File eng/Test-Live.ps1
pwsh -File eng/Test-Live.ps1 -Suite Parameters
pwsh -File eng/Test-Live.ps1 -Suite All
pwsh -File eng/Test-Live.ps1 -Suite Parameters -Scenario Reasoning
```

On Windows, missing process settings are filled from current-user environment variables. Explicit process values take precedence. No `.env` auto-loading. The key never belongs in this README, the template, logs or source files.

The default test address is `https://ark.cn-beijing.volces.com/api/v3/`, with an explicit override available in `LiveLlmContext`. Parameters and limits are test constants, not additional environment variables. There is a 90-second deadline per call including full streaming, a five-request cap per context and a 32-request atomic hard cap per process. No automatic retry or protocol/model fallback. Smoke uses 128 output tokens; parameters normally use 512, reasoning tests one.

One test performs five requests: Chat JSON, Chat streaming, Responses creation, Responses streaming and continuation through a newly built client using the same Session/cache. Assertions check actual text/JSON, usage and completion, final HTTP payload fields and retained upstream context. A synthetic random marker tests continuation without resending message history. The Responses creation call explicitly stores that synthetic context upstream so continuation can be exercised.

`Smoke` (default) contains one case/five requests. `Parameters` permits up to 26 requests covering strict schemas, tool calls with result return, reasoning on/off, tool choice none, output caps and stateless encrypted reasoning replay. `All` permits up to 31 requests. Use `-Scenario Structured`, `Tools`, `ToolChoice`, `Reasoning`, `Limits` or `Replay` with `Parameters` or `All` to run only that group.

**Known upstream mismatch:** `doubao-seed-evolving` returns function calls for correctly transmitted `tool_choice="none"` on both endpoints. Those two assertions remain strict and failing, so `ToolChoice`, `Parameters` and `All` currently exit nonzero. Latest per-case evidence has 20 passes and two failures, combining a full run with a targeted reasoning rerun. See [the verification record](../../docs/llm-parameter-verification.md).

Reports under ignored `artifacts/live/volcengine-llm/` record sanitized pass/fail, assertion evidence and HTTP statuses. Do not treat a partial report or an opt-out skip as success. They exclude credentials, raw reasoning and encrypted blocks. The fixture uses `MemoryDistributedCache` and an in-process test coordinator; it does not test real Redis, cross-process locks, every model parameter or other providers. See [the full test contract](../../docs/testing.md).
