# Asgard.Loki.LLM.Perplexity

Native Perplexity Responses and Sonar Chat JSON/SSE using the existing LLM clients. Separate providers own their original protocol state and explicit endpoint selection. This package references only LLM, Infrastructure and standard HTTP factory support.

## Support matrix

| Operation | Support |
| --- | --- |
| Native Responses create | Complete original response, output items, native status, usage and unknown fields |
| Native Responses stream | Original event JSON, SSE event/id/retry, native search/reasoning events and sparse failure |
| Native Sonar Chat | Full original JSON and SSE, citations/search results, native reasoning/tool history, media references and unknown fields |
| Sonar endpoint selection | Explicit `chat/completions` or `v1/sonar`, appended to the complete configured root |
| Authentication | Explicit independent API Key, selected request-local Bearer |
| Sessions | Complete server/model/credential/provider/client/policy/version binding; explicit native continuation |
| Compaction, WebSocket and Realtime | Not implemented by these providers |
| Responses model chains, presets and profiles | Rejected: these selectors can override the bound actual model |

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

## Sonar Chat initialization

```csharp
var sonar = new PerplexitySonarProvider(factory, new PerplexitySonarOptions
{
    Endpoint = PerplexitySonarEndpoint.ChatCompletions
    // Explicit alternative: PerplexitySonarEndpoint.SonarV1
});
await using var sonarClient = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [sonar],
    Credentials = [new LlmCredential("sonar-key", selectedSonarKey)],
    Routes = [new LlmRoute
    {
        Model = "native-sonar", Targets = [new LlmTarget
        {
            Id = "sonar-target", ProviderId = sonar.Id,
            BaseAddress = new Uri("https://api.perplexity.ai/"),
            UpstreamModel = selectedSonarModel, CredentialId = "sonar-key",
            Protocols = LlmProtocol.ChatCompletions
        }]
    }]
}).Build();
var completion = await sonarClient.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "native-sonar",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new LlmCallOptions { SessionId = "sonar-conversation-1" }, cancellationToken);
// completion.Data contains full native choices, citations, search results and usage.
```

Sonar defaults to provider ID `perplexity-sonar`, named client `Asgard.Loki.LLM.Perplexity.Sonar`, and `ChatCompletions`. The [official Python SDK](https://github.com/perplexityai/perplexity-py/blob/1d4ba52e875a858a12fc26f854ae9470b8496835/src/perplexity/generated/api.py) calls `chat/completions`; the [official native request examples](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/how-to) also show `v1/sonar`. Both options use an origin root as above, or an explicit proxy prefix. Endpoint selection participates in Session binding and never changes automatically. Responses and Sonar use separate complete targets/providers and native histories.

Sonar forwards complete messages, original content containers and remote media references, native reasoning_steps, reversed tool results/call IDs/argument strings, response_format schema/regex parameters, search filters, web_search_options, stream_mode and unknown values. It does not clamp top_p, rename max_tokens, select a model, move search settings to tools or migrate a Chat request to Agent Responses. Original returned citations/search_results/images/related_questions/usage remain data; the SDK neither fetches them nor calculates billing. Perplexity [documents continued synchronous/streaming Sonar requests through server-side reformulation](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/overview); actual model availability and setting effects remain upstream-owned.

Sonar preserves native `message`, `info` and `end_of_stream` frames, including cumulative message snapshots alongside deltas. An explicit end_of_stream or actual DONE ends the call; otherwise normal EOF requires every observed choice to have an original finish_reason. DONE cannot precede all native data, repeat or be followed by data. An end_of_stream frame cannot report PENDING, repeat or reopen generation. Native info/unknown metadata can follow a global end before DONE/EOF. Sparse int64 choice indices are tracked with a bounded dictionary, never renumbered; unfinished choices may continue after another finishes. A final global message snapshot may repeat the same original finish reason but cannot regenerate an ended delta or contradict its finish. No finish reason, message, global end or DONE is manufactured. From the first ending, the buffered tail waits for EOF, owned-resource closure and final cancellation. A complete JSON result retains original status/optional finish reasons and does not start a poller.

## Native Responses outcomes and stream endings

A complete JSON response retains `completed`, `failed`, `incomplete`, `queued`, `in_progress` or `cancelled` as reported. A queued result does not start a background poller. HTTP failures and connection-level error envelopes use `LlmUpstreamException`, preserving actual HTTP status, raw `Body` bytes and content type; `ResponseBody` decodes strict UTF-8.

Native `response.failed` is a protocol outcome with top-level `error.message`; it need not contain a response snapshot or ID. It is returned unchanged. `response.completed` may have a partial or absent response snapshot. Its native terminal status remains original; no ID, output, status or completion event is fabricated. Known native events require strictly increasing exact int64 sequence numbers and consistent response IDs when provided. Tool/item/transport IDs are separate data and are never renumbered. Unknown events, including similarly named business metadata, remain original.

EOF must follow a native completed/failed event. An actual optional `[DONE]` is consumed only after that terminal and cannot repeat or precede more events. Known generation events cannot reopen an ended response. The terminal and following metadata wait for normal EOF, exact declared length, source/response/client/request closure and final cancellation. Late transport, parsing, native-state, disposal or cancellation failures prevent that buffered tail from being delivered. Early enumeration exit releases owned call resources.

## Local resource policy and ownership

`PerplexityResponsesOptions` and `PerplexitySonarOptions` default to 32MiB each for MaximumRequestBytes, MaximumResponseBytes and MaximumStreamBytes, and 128 for MaximumBufferedTerminalEvents. Sonar additionally defaults MaximumTrackedChoices to 128. Positive limits, the named client and the Sonar endpoint option participate in provider identity. Strict UTF-8, raw duplicate fields, actual media/length and request URI/method/Host/auth/content are checked. Foreign default Key/signature headers are rejected. Selected Bearer/Host/Accept override same-name defaults without modifying the borrowed factory. Reflect semantic named-client changes in target Version and disable POST retries/hedging in the application-owned HTTP pipeline.

For both providers an explicit SSE `event: error` retains its original UTF-8 data as LlmUpstreamException Body, even when it is plain text or equals `[DONE]`. It is checked before control-marker or JSON interpretation. A later explicit connection error discards any buffered native terminal.

Factory, Session cache and explicit binding coordinator are borrowed; requests, frozen content, returned clients, responses and body streams belong to each call. The SDK performs no local search, tool execution, media fetching, history storage, remote model fallback, polling, file management or billing calculation. Original native search/tool/results/usage remain caller-consumed data.

## Verification

Offline xUnit v3 tests use independent complete JSON and literal SSE windows, substitute HTTP, MemoryDistributedCache and an explicit test coordinator. They cover the public SDK, both Sonar endpoint selections and proxy roots, native sparse failures, partial outcomes, cumulative message/delta frames, citation metadata, tool associations, exact unknown data, lifecycle corruption, plain-text connection errors, resource boundaries and cross-instance Session binding. No real Perplexity Key, live model, endpoint entitlement or setting effect has been tested.
