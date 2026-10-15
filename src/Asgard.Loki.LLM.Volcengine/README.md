# Asgard.Loki.LLM.Volcengine

Volcengine Ark LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |

Create `VolcengineLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Targets reference its `Id` and independently configure the server, actual model and credential. A typical base address is `https://ark.cn-beijing.volces.com/api/v3/`.

Uses the named client `Asgard.Loki.LLM.Volcengine`; disable generation POST retries/hedging in that client's configuration. Raw tools, reasoning, caching parameters, IDs, usage, errors and unknown events are retained. Response streams require an actual terminal event.

No native Responses WebSocket, protocol-to-protocol conversion or hard-coded model catalog. Tests use local HTTP stubs; live Ark model permissions must be verified separately.

Depends only on the LLM capability and standard Microsoft HTTP components. See the repository root README for a complete initialization example.
