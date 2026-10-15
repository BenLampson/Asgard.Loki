# Volcengine LLM live integration tests

An explicitly invoked xUnit v3 smoke suite using the public Loki client and actual standard Ark HTTPS endpoints. Excluded from the ordinary solution and `eng/Test.ps1` discovery.

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
```

On Windows, missing process settings are filled from current-user environment variables. Explicit process values take precedence. No `.env` auto-loading. The key never belongs in this README, the template, logs or source files.

The default test address is `https://ark.cn-beijing.volces.com/api/v3/`, with an explicit override available in `LiveLlmContext`. Other parameters are test constants: five requests maximum, 90 seconds per call including full streaming, 128 output tokens, disabled thinking, and temperature 0.1. No automatic retry or protocol/model fallback.

One test performs five requests: Chat JSON, Chat streaming, Responses creation, Responses streaming and continuation through a newly built client using the same Session/cache. Assertions check actual text/JSON, usage and completion, final HTTP payload fields and retained upstream context. A synthetic random marker tests continuation without resending message history. The Responses creation call explicitly stores that synthetic context upstream so continuation can be exercised.

Reports under ignored `artifacts/live/volcengine-llm/` record sanitized pass/fail, completed scenarios and HTTP statuses. Do not treat a partial report or an opt-out skip as success. A completed local run with `doubao-seed-evolving` passed all five requests. The fixture uses `MemoryDistributedCache` and an in-process test coordinator; it does not test real Redis, cross-process locks, every model parameter or other providers. See [the full test contract](../../docs/testing.md).
