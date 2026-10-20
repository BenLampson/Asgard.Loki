# Asgard.Loki.LLM.Volcengine

Volcengine Ark LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |

Create `VolcengineLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Targets reference its `Id` and independently configure the server, actual model and credential. A typical base address is `https://ark.cn-beijing.volces.com/api/v3/`.

Uses the named client `Asgard.Loki.LLM.Volcengine`; disable generation POST retries/hedging in that client's configuration. Raw tools, reasoning, caching parameters, IDs, usage, errors and unknown events are retained. Response streams require an actual terminal event.

No native Responses WebSocket, protocol-to-protocol conversion or hard-coded model catalog. The provider has 93 offline tests using local HTTP stubs. Separate real standard-Ark checks with `doubao-seed-evolving` passed Chat/Responses JSON/SSE, strict JSON Schema, tool calls with result return, thinking on/off, output limits, response continuation and opaque reasoning replay.

**Known live failure:** both protocols can return a function call despite a correctly transmitted `tool_choice="none"`; the strict integration tests remain failing. Loki preserves this upstream result and does not execute tools. Ark Responses currently reports token-limit truncation as `incomplete_details.reason="length"`, retained unchanged. See the [verification record](https://github.com/BenLampson/Asgard.Loki/blob/dev/docs/llm-parameter-verification.md) for the precise cases and limitations. Other accounts/models and Redis cross-process correctness require separate verification.

Depends only on the LLM capability and standard Microsoft HTTP components. See the repository root README for a complete initialization example.

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.
