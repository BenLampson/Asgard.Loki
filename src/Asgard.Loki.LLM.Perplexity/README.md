# Asgard.Loki.LLM.Perplexity

Native Perplexity Responses JSON/SSE using the existing LLM Responses client. The provider appends `responses` to the explicitly selected complete root; the native API accepts `/v1/responses`. It references only LLM, Infrastructure and standard HTTP factory support.

## Support matrix

| Operation | Support |
| --- | --- |
| Native Responses create | Complete original response, output items, native status, usage and unknown fields |
| Native Responses stream | Original event JSON, SSE event/id/retry, native search/reasoning events and sparse failure |
| Authentication | Explicit independent API Key, selected request-local Bearer |
| Sessions | Complete server/model/credential/provider/client/policy/version binding; explicit native continuation |
| Chat, compaction, WebSocket and Realtime | Not implemented by this provider |
| Model chains, presets and profiles | Rejected: these selectors can override the bound actual model |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Perplexity;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedNativeModel and selectedApiKey are caller-owned.
var provider = new PerplexityResponsesProvider(factory);
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("native-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "native-responses", Targets = [new LlmTarget
        {
            Id = "selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://api.perplexity.ai/v1/"),
            UpstreamModel = selectedNativeModel, CredentialId = "native-key",
            Protocols = LlmProtocol.Responses
        }]
    }]
}).Build();
var result = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = "native-responses", Input = JsonValue.Create("Hello")
}, new LlmCallOptions { SessionId = "conversation-1" }, cancellationToken);
// result.Data is the complete native response, including its original status.
// Responses.StreamAsync returns unassembled native events.
```

Default provider ID is `perplexity`; named client is `Asgard.Loki.LLM.Perplexity`. Explicit proxy roots work without hostname inference. Configure a native model that the upstream accepts for your account. The provider appends only `responses`; it does not switch endpoints, choose models or migrate a Sonar request. See the official [native API schema](https://docs.perplexity.ai/api-reference/agent-post) and [Responses alias documentation](https://docs.perplexity.ai/docs/agent-api/openai-compatibility).

Only the selected root `model` and method `stream` flag are replaced. String/array input, complete tool history, reversed function outputs, call IDs, thought signatures, `reasoning`, native root `response_format`, `store`, explicit `previous_response_id` and unknown exact/null/false/zero values remain original. `models`, `preset` and `profile` root selectors, including case aliases, are rejected before client creation because they can replace the selected model. Native optional-setting acceptance remains upstream-owned. Hidden CLR containers inside JsonValue are rejected; use explicit JSON containers.

## Native outcomes and stream endings

A complete JSON response retains `completed`, `failed`, `incomplete`, `queued`, `in_progress` or `cancelled` as reported. A queued result does not start a background poller. HTTP failures and connection-level error envelopes use `LlmUpstreamException`, preserving actual HTTP status, raw `Body` bytes and content type; `ResponseBody` decodes strict UTF-8.

Native `response.failed` is a protocol outcome with top-level `error.message`; it need not contain a response snapshot or ID. It is returned unchanged. `response.completed` may have a partial or absent response snapshot. Its native terminal status remains original; no ID, output, status or completion event is fabricated. Known native events require strictly increasing exact int64 sequence numbers and consistent response IDs when provided. Tool/item/transport IDs are separate data and are never renumbered. Unknown events, including similarly named business metadata, remain original.

EOF must follow a native completed/failed event. An actual optional `[DONE]` is consumed only after that terminal and cannot repeat or precede more events. Known generation events cannot reopen an ended response. The terminal and following metadata wait for normal EOF, exact declared length, source/response/client/request closure and final cancellation. Late transport, parsing, native-state, disposal or cancellation failures prevent that buffered tail from being delivered. Early enumeration exit releases owned call resources.

## Local resource policy and ownership

`PerplexityResponsesOptions` defaults to 32MiB each for MaximumRequestBytes, MaximumResponseBytes and MaximumStreamBytes, and 128 for MaximumBufferedTerminalEvents. Positive limits and the named client participate in provider identity. Strict UTF-8, raw duplicate fields, actual media/length and request URI/method/Host/auth/content are checked. Foreign default Key/signature headers are rejected. Selected Bearer/Host/Accept override same-name defaults without modifying the borrowed factory. Reflect semantic named-client changes in target Version and disable POST retries/hedging in the application-owned HTTP pipeline.

Factory, Session cache and explicit binding coordinator are borrowed; requests, frozen content, returned clients, responses and body streams belong to each call. The SDK performs no local search, tool execution, media fetching, history storage, remote model fallback, polling, file management or billing calculation. Original native search/tool/results/usage remain caller-consumed data.

## Verification

Offline xUnit v3 tests use independent complete JSON and literal SSE windows, substitute HTTP, MemoryDistributedCache and an explicit test coordinator. They cover the public SDK, native sparse failures, partial outcomes, tool associations, exact unknown data, lifecycle corruption, resource boundaries and cross-instance Session binding. No real Perplexity Key, live model, endpoint entitlement or setting effect has been tested.
