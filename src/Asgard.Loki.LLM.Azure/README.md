# Asgard.Loki.LLM.Azure

Native Azure OpenAI model calls for the existing LLM capability. This provider references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard Microsoft HTTP components. It does not manage deployments, accounts, billing or a forwarding server.

## Support matrix

| Operation | v1 API | Explicit dated API |
| --- | --- | --- |
| Chat Completions, complete JSON | `chat/completions`; deployment in body `model` | `deployments/{deployment}/chat/completions`; no body `model` |
| Responses creation / caller-owned continuation, complete JSON | `responses` | `responses`; deployment in body `model` |
| Streaming SSE | Native Chat / Responses events | Native Chat / Responses events |
| Explicit Responses compaction, complete JSON | `responses/compact`; target feature required | Same relative endpoint with explicit version; offline routing evidence only |
| WebSocket / Realtime | Not implemented | Not implemented |
| Authentication | Selected `api-key`, or borrowed external Bearer token source | Same explicit choice |

For v1, provide an API root ending in `/openai/v1/`; `ApiVersion` is omitted unless explicitly configured. For dated APIs, provide a root ending in `/openai/` and a nonempty `ApiVersion`. Proxy prefixes are retained. The SDK does not infer a host, version, deployment name, token scope or cloud from model names. Deployment dots are retained; path/query components are escaped. Account/model/API-version availability remains caller-owned.

## Initialize

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Azure;

// Borrow application-owned httpFactory and sessionStore.
var azure = new AzureLlmProvider(httpFactory);
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [azure],
    Credentials = [new LlmCredential("azure-key", apiKey)],
    SessionStore = sessionStore,
    Routes = [new LlmRoute
    {
        Model = "public-model",
        Targets = [new LlmTarget
        {
            Id = "azure-deployment", ProviderId = azure.Id,
            BaseAddress = new Uri("https://resource.openai.azure.com/openai/v1/"),
            UpstreamModel = "configured-deployment", CredentialId = "azure-key",
            Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses,
            // Declare only if this deployment/API version supports compaction.
            Features = LlmTargetFeatures.ResponsesCompaction
        }]
    }]
}).Build();

var result = await loki.LLM.ChatCompletions.CompleteAsync(
    new ChatCompletionRequest
    {
        Model = "public-model",
        Messages = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "hello" })
    }, new LlmCallOptions { SessionId = "application-session" }, cancellationToken);
```

Use `loki.LLM.Responses.CreateAsync` for native Responses; continuation uses the original response ID and the same Session. The application supplies `IDistributedCache` and an explicit first-binding coordinator when constructing `sessionStore`; plain cache Get/Set is not atomic.

Select dated routing explicitly with `new AzureLlmOptions { ApiStyle = AzureLlmApiStyle.Dated, ApiVersion = "your-supported-version" }` and the corresponding `/openai/` target root. An explicitly configured v1 version is passed as a query value without rewriting other fields.

## Explicit native compaction

Call `loki.LLM.Responses.CompactAsync(new ResponseCompactionRequest { Model = "public-model", Input = nativeInput }, new LlmCallOptions { SessionId = "application-session" }, cancellationToken)` only after declaring `LlmTarget.Features = LlmTargetFeatures.ResponsesCompaction`. The operation uses the bound deployment and the selected API root's `responses/compact` endpoint; it sends no `stream` field and adds no automatic compaction, pruning or history lookup. `Input` may be native text, an item array or null/absent. An optional `PreviousResponseId` must be owned by the same complete Session; it never selects another target.

The complete `ResponseCompaction.Data` and `Output` retain original messages, reasoning/encrypted state, function calls/results and their `call_id` associations, opaque compaction items, usage and unknown fields. The application chooses when to pass the entire returned output window to a subsequent native Responses call. A compaction ID is not registered as a generated-response ID for continuation. The SDK does not decrypt, rebuild or execute the window.

`MaximumCompactionRequestBytes` is an optional positive UTF-8 request-byte limit, effectively 32 MiB when omitted. The final request is snapshotted and checked before token acquisition; `MaximumBodyBytes` bounds the full response/error. Compaction rejects SSE before body reads, checks actual response target/method, strict UTF-8/JSON/duplicate properties, media type/charset, declared length, EOF and owned resource closure before publication. Upstream HTTP and root native errors retain original raw bytes, content type and actual status, including binary error bodies. External source identity and token expiry are rechecked after I/O and before publication; no token refresh/retry occurs.

The default omitted request limit preserves existing Azure SSE binding bytes. An explicit limit participates in provider identity and invalidates affected old Sessions, even when equal to the effective default. Availability depends on the configured deployment and API version. The primary [Azure compaction example](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses#compact-a-response) documents the v1 operation. Explicit dated-root routing is covered offline only; the SDK does not assert that an arbitrary dated API version accepts compaction.

## External authentication

Register `LlmCredential.External("azure-identity", "configured-tenant/principal")`, set the target's `CredentialId` accordingly, and provide `new AzureLlmProvider(httpFactory, tokenSource: source)`. The application implements borrowed `IAzureLlmTokenSource`:

- `BindingIdentity` identifies stable tenant/audience/authentication configuration, never an access token. Changing it on the same source instance fails calls; register a new provider for changed configuration. New configuration invalidates old Sessions.
- `GetTokenAsync(principalIdentity, cancellationToken)` returns `AzureLlmAccessToken` with the same principal, raw Bearer content and expiry. The provider calls it once per explicit operation and checks expiry both after acquisition and immediately before HTTP send.
- The source owns actual login, scope selection, refresh/cache policy and the truth of its principal assertion. The SDK does not parse JWTs, discover environment credentials or supply a cloud identity implementation. No token enters the request body, Session fingerprint or default token JSON/display.
- Token refresh under the same principal retains the original complete target. A principal change rejects an existing Session before acquiring another token. Missing, expired or mismatched tokens fail without implicit refresh, replay or fallback.

API Key mode never invokes the token source. Anonymous credentials and external credentials without a source are rejected. Request-selected authentication overrides a same-kind default header; a factory default containing the other authentication kind fails before send. Factories must not add authentication through hidden handlers or automatic retries/hedging.

## Fidelity, limits and ownership

For complete Chat/Responses generation, configured deployment and method-selected `stream=false` are set; dated Chat moves deployment into the URL. Messages, tool IDs and arguments, strict schemas, reasoning/encrypted items, native filter results, usage, exact JSON numbers and unknown fields remain native. The original caller request is snapshotted before awaited authentication. Result metadata distinguishes the configured deployment from the original returned model.

Complete JSON waits for normal body EOF, matching declared length, response/client disposal and final cancellation checks. `MaximumBodyBytes` defaults to 32 MiB and limits the total raw JSON/error/SSE response bytes, including SSE comments and metadata. It is a total-operation bound, not a per-event quota; configure it explicitly for larger streams. Duplicate JSON properties, invalid UTF-8/Unicode escapes, truncated JSON, non-object results and SSE media fail. HTTP failures retain actual status/raw body; a root native error at HTTP 200 remains an upstream error with that status. A valid complete JSON result is protocol evidence, not proof of answer quality or tool execution.

The provider borrows the HTTP factory, token source and optional `TimeProvider`; operation-created clients/messages/responses are disposed. It adds no retries, target switching or token-source retry. Configure the named client `Asgard.Loki.LLM.Azure` with suitable timeouts and no generation retry/hedging. Native streaming uses `ChatCompletions.StreamAsync` or `Responses.StreamAsync`; incremental native records are delivered as they arrive, with terminal handling described below.

Cross-protocol conversion requires explicit `ConvertingLlmProvider` registration from the separate conversion package. Azure adds no automatic conversion or model suffix heuristics. Native Azure filter metadata and unrepresentable fields retain the conversion package's strict default/loss diagnostics; this provider's native preservation does not establish cross-protocol equivalence.

## Native SSE lifecycle

Chat preserves empty identity prompt/filter annotations and metadata after `finish_reason`. The first choice finish starts a finite tail buffer; all subsequent records retain their original order, including usage and delayed filter signals. Each observed choice must finish and `[DONE]` must arrive, followed by normal EOF and disposal. A delayed `content_filter` remains that native reason; it is never rewritten to a successful stop. Choice indices and tool deltas are not reordered, renumbered or assembled by the provider. Unknown native objects remain raw.

Responses retains unknown nonterminal events and original function `item_id` / `call_id` data. Native `response.completed`, `response.incomplete`, `response.failed` and `error` are held until EOF/disposal; incomplete and failed are not upgraded to completed. A terminal response snapshot must retain the same response identity and matching native status. Missing/repeated terminal events, changed response identity or events after termination fail. An optional `[DONE]` can follow a genuine Responses terminal; it cannot substitute for one. Root native errors remain upstream exceptions with their raw data.

Both modes use strict UTF-8/JSON and a total raw-byte bound before the SSE line parser. `MaximumBufferedTerminalEvents` defaults to 128; `MaximumTrackedChoices` defaults to 128. Hitting a limit fails instead of dropping records. Early consumer exit disposes owned HTTP resources and creates no completion/retry/reconnection. SSE IDs and retry hints remain metadata only. This POST-create stream does not implement background retrieval/resume, steering, WebSocket or Realtime. Compaction is a separate explicit complete-JSON operation.

The SSE implementation and new resource policies change provider binding identity from the preceding JSON-only contract. Those older Sessions require new IDs or caller-controlled expiry/removal; the SDK never silently rebinds them. Existing core API Key/anonymous fingerprint algorithms and conversion-v24 remain unchanged.

See the primary [Azure streaming examples](https://learn.microsoft.com/en-us/azure/foundry/openai/concepts/content-streaming) for delayed annotation envelopes and the [Responses reference](https://learn.microsoft.com/en-us/rest/api/aifoundry/azureopenai/responses) for native response data. Local fixtures verify protocol handling only.

## Verification status

250 offline xUnit v3 cases (86 complete-JSON/authentication, 69 SSE/configuration and 95 native compaction cases) use independent complete wire/result fixtures, fake HTTP, a deterministic clock and `MemoryDistributedCache` with explicit coordination. They cover both API styles and operations, both authentication modes, refreshed-token Session/continuation affinity, configuration/principal drift, strict byte/JSON/length boundaries, cancellation, resource failures and explicit conversion integration. Independent raw SSE and complete event-sequence goldens additionally cover native filters after finish, unknown events, tool/item association IDs, SSE identity/retry metadata, exact numbers and terminal ordering. Truncated/repeated/late records, wrong identity/status, invalid encoding, length/limit boundaries, early consumer exit and EOF/disposal cancellations retain explicit failure tests. Fixtures retain native filters, tool associations, opaque reasoning and large integers. Compaction cases additionally verify full windows, both authentication modes and all explicit endpoint profiles, request/response byte limits, binary/native errors, fixed-target drift, post-I/O authentication expiry/configuration drift, final failures, Session reconstruction and response ownership. No Azure login, real access-token refresh or live model call has been performed.

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.
