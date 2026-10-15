# Asgard.Loki.LLM.Volcengine

Volcengine Ark LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |

Create `VolcengineLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Targets reference its `Id` and independently configure the server, actual model and credential. A typical base address is `https://ark.cn-beijing.volces.com/api/v3/`.

Uses the named client `Asgard.Loki.LLM.Volcengine`; disable generation POST retries/hedging in that client's configuration. Raw tools, reasoning, caching parameters, IDs, usage, errors and unknown events are retained. Response streams require an actual terminal event.

No native Responses WebSocket, protocol-to-protocol conversion or hard-coded model catalog. Unit tests use local HTTP stubs. The separate Volcengine LLM integration suite has passed five real standard-Ark requests with `doubao-seed-evolving`: Chat JSON/SSE, Responses JSON/SSE and response continuation after rebuilding LokiClient. Other accounts, models and parameter combinations require their own verification; the fixture does not establish Redis cross-process correctness.

Depends only on the LLM capability and standard Microsoft HTTP components. See the repository root README for a complete initialization example.
