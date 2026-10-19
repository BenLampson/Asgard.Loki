# Asgard.Loki.Image.OpenAI

OpenAI Images Generations provider for Asgard.Loki.

| Feature | Support |
| --- | --- |
| `POST /v1/images/generations` | Yes |
| Complete JSON results | Yes |
| SSE partial/final image events | Yes, when supported by the upstream model |
| Base64 and URL outputs | Preserved; Base64 decoding is available on demand |
| Quality, size, transparency, output format, image count and future parameters | Passed through |
| Image edits / variations | Provider transport not implemented; Image capability has separate custom-adapter edit contracts |

Create `OpenAiImageAdapter(IHttpClientFactory, OpenAiImageOptions)` and register it in `ImageOptions.Routes`. Configure the actual upstream model and API Key in the adapter options; use the route alias in requests. Optional organization/project headers and a custom base address are supported.

The default named HTTP client is `Asgard.Loki.Image.OpenAI`. Disable generation POST retries and hedging. Prompts and unknown JSON fields are not rewritten. Incomplete streams fail instead of claiming completion. URLs are returned without automatic downloads.

Local tests verify request/response and SSE behavior. No live image generation or provider entitlement check is performed by those tests.
