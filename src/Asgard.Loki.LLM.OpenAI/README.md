# Asgard.Loki.LLM.OpenAI

OpenAI LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |

Create `OpenAiLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Configure targets with a `/v1/` base address, actual model and credential. The default provider ID is `openai`; the named HTTP client is `Asgard.Loki.LLM.OpenAI`.

Preserves extension parameters, tools, reasoning items, cache controls, original response IDs, usage, error events and unknown JSON. Session routing uses the shared LLM module. Configure project/organization defaults through the named HTTP client if needed. Disable POST retries and hedging.

Native Responses WebSocket, automatic history reconstruction and durable replay are not implemented. Tests verify local HTTP/SSE behavior, not a live OpenAI account.

For OpenAI Images Generations, install the independent `Asgard.Loki.Image.OpenAI` package. This LLM provider does not pull in image or audio dependencies.
