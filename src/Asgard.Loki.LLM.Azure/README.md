# Asgard.Loki.LLM.Azure

Native Azure OpenAI model calls for the existing LLM capability. This provider references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not manage deployments, accounts, billing or a forwarding server.

## Support matrix

| Operation | v1 API | Explicit dated API |
| --- | --- | --- |
| Chat Completions, complete JSON | `chat/completions`; deployment in body `model` | `deployments/{deployment}/chat/completions`; no body `model` |
| Responses creation / caller-owned continuation, complete JSON | `responses` | `responses`; deployment in body `model` |
| Streaming SSE | Pending separate checkpoint | Pending separate checkpoint |
| Responses compaction / WebSocket / Realtime | Not implemented | Not implemented |
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
            Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses
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

## External authentication

Register `LlmCredential.External("azure-identity", "configured-tenant/principal")`, set the target's `CredentialId` accordingly, and provide `new AzureLlmProvider(httpFactory, tokenSource: source)`. The application implements borrowed `IAzureLlmTokenSource`:

- `BindingIdentity` identifies stable tenant/audience/authentication configuration, never an access token. Changing it on the same source instance fails calls; register a new provider for changed configuration. New configuration invalidates old Sessions.
- `GetTokenAsync(principalIdentity, cancellationToken)` returns `AzureLlmAccessToken` with the same principal, raw Bearer content and expiry. The provider calls it once per explicit operation and checks expiry both after acquisition and immediately before HTTP send.
- The source owns actual login, scope selection, refresh/cache policy and the truth of its principal assertion. The SDK does not parse JWTs, discover environment credentials or supply a cloud identity implementation. No token enters the request body, Session fingerprint or default token JSON/display.
- Token refresh under the same principal retains the original complete target. A principal change rejects an existing Session before acquiring another token. Missing, expired or mismatched tokens fail without implicit refresh, replay or fallback.

API Key mode never invokes the token source. Anonymous credentials and external credentials without a source are rejected. Request-selected authentication overrides a same-kind default header; a factory default containing the other authentication kind fails before send. Factories must not add authentication through hidden handlers or automatic retries/hedging.

## Fidelity, limits and ownership

Only configured deployment and method-selected `stream=false` are set; dated Chat moves deployment into the URL. Messages, tool IDs and arguments, strict schemas, reasoning/encrypted items, native filter results, usage, exact JSON numbers and unknown fields remain native. The original caller request is snapshotted before awaited authentication. Result metadata distinguishes the configured deployment from the original returned model.

Complete JSON waits for normal body EOF, matching declared length, response/client disposal and final cancellation checks. `MaximumBodyBytes` defaults to 32 MiB and limits raw response/error bytes. Duplicate JSON properties, invalid UTF-8/Unicode escapes, truncated JSON, non-object results and SSE media fail. HTTP failures retain actual status/raw body; a root native error at HTTP 200 remains an upstream error with that status. A valid complete JSON result is protocol evidence, not proof of answer quality or tool execution.

The provider borrows the HTTP factory, token source and optional `TimeProvider`; operation-created clients/messages/responses are disposed. It adds no retries, target switching or token-source retry. Configure the named client `Asgard.Loki.LLM.Azure` with suitable timeouts and no generation retry/hedging. Streaming currently throws `NotSupportedException`; it is not simulated by buffering complete JSON.

Cross-protocol conversion requires explicit `ConvertingLlmProvider` registration from the separate conversion package. Azure adds no automatic conversion or model suffix heuristics. Native Azure filter metadata and unrepresentable fields retain the conversion package's strict default/loss diagnostics; this provider's native preservation does not establish cross-protocol equivalence.

## Verification status

86 offline xUnit v3 cases use independent complete wire/result fixtures, fake HTTP, a deterministic clock and `MemoryDistributedCache` with explicit coordination. They cover both API styles and operations, both authentication modes, refreshed-token Session/continuation affinity, configuration/principal drift, strict byte/JSON/length boundaries, cancellation, resource failures and explicit conversion integration. Fixtures retain native filters, tool associations, opaque reasoning and large integers. No Azure login, real access-token refresh or live model call has been performed. Windows/Linux CI remains required for PR/release; this development checkpoint runs local verification only.
