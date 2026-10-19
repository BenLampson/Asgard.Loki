# Asgard.Loki.LLM.GoogleVertex

Native Vertex Gemini calls for the existing LLM capability. This independent supplier package references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not depend on the Gemini Developer API supplier, Azure, Conversion, a cloud identity SDK or another capability.

## Support matrix

| Operation | Status |
| --- | --- |
| Standard Vertex `generateContent`, complete JSON | Implemented |
| Publisher-model resource form | Explicit `projects/{project}/locations/{location}/publishers/{publisher}/models/{model}`; Google Gemini fixtures |
| Tuned endpoint resource form | Explicit `projects/{project}/locations/{location}/endpoints/{endpoint}`; complete native Gemini JSON |
| External Bearer authentication | Borrowed application token source with stable principal/configuration |
| `streamGenerateContent` SSE | Pending separate checkpoint |
| Express-mode API Key / short resource root | Not implemented by this provider |
| Claude `rawPredict` / `streamRawPredict`, OpenAI-compatible MaaS | Not implemented by this provider |
| Live / Interactions / async batch / cloud resource management | Not implemented |

The target's `BaseAddress` is an explicit versioned API root, such as `https://us-central1-aiplatform.googleapis.com/v1/` or a caller-owned proxy root. `UpstreamModel` is the **complete raw resource name**, including project and location. Each component is escaped separately. The provider does not infer a region/hostname, normalize a model suffix, map marketing names or choose another native API based on publisher/model text. A configured publisher model must support the selected generateContent operation; resource syntax alone is not proof of model availability.

## Initialize

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.GoogleVertex;
using System.Text.Json.Nodes;

// Application-owned httpFactory, tokenSource and coordinated sessionStore.
var vertex = new GoogleVertexLlmProvider(httpFactory, tokenSource);
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [vertex],
    Credentials = [LlmCredential.External("vertex-identity", "configured-principal")],
    SessionStore = sessionStore,
    Routes = [new LlmRoute
    {
        Model = "public-model",
        Targets = [new LlmTarget
        {
            Id = "vertex-target", ProviderId = vertex.Id,
            BaseAddress = new Uri("https://us-central1-aiplatform.googleapis.com/v1/"),
            UpstreamModel = "projects/your-project/locations/us-central1/publishers/google/models/your-model",
            CredentialId = "vertex-identity", Protocols = LlmProtocol.Gemini
        }]
    }]
}).Build();

var result = await loki.LLM.Gemini.GenerateAsync(new GeminiContentRequest
{
    Model = "public-model",
    Contents = new JsonArray(new JsonObject
    {
        ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "hello" })
    })
}, new LlmCallOptions { SessionId = "application-session" }, cancellationToken);
```

Construct `sessionStore` with application-owned `IDistributedCache` plus an explicit first-binding coordinator. Plain Get/Set is not an atomic distributed first binding. Server, API root/version, complete project/location/model resource, credential principal and provider configuration all participate in Session affinity. Token rotation preserves this binding; changed resources, principal or configuration reject an existing Session instead of selecting another target.

## Authentication and ownership

The application implements `IGoogleVertexLlmTokenSource` and supplies a stable `BindingIdentity` for its configured scopes/audience/principal-resolution policy. `GetTokenAsync(principalIdentity, cancellationToken)` returns `GoogleVertexLlmAccessToken` with the same asserted principal, raw Bearer content and expiry. The source owns actual login, workload/service-account identity, scope selection, refresh/cache policy and the truth of its principal assertion. This provider does not read environment credentials, JSON service-account files or metadata servers, and does not parse JWTs.

The provider calls the source once per explicit generation, rejects missing/mismatched/expired results, checks source identity before and after acquisition, and checks expiry again immediately before sending. Source configuration drift requires a newly registered provider; its binding identity invalidates previous Sessions. Expired tokens and HTTP authentication failures do not cause refresh retries, request replay or target switching. Access tokens are absent from body, Session fingerprints and default token JSON/display. API Key and anonymous credential kinds fail registration/direct dispatch.

Optional `GoogleVertexLlmOptions.QuotaProjectId` sends exactly the configured `x-goog-user-project` header and participates in the provider fingerprint. It is request identity configuration, not a cloud billing service. It is never inferred from resource project or principal. Unselected default API Key or quota-project headers fail before HTTP send. The request's selected Bearer and optional quota header override same-kind factory defaults without mutating the factory.

The HTTP factory, token source and optional `TimeProvider` remain borrowed. Operation-created clients/messages/responses are disposed. Configure `Asgard.Loki.LLM.GoogleVertex` with suitable timeouts and no automatic retries/hedging or hidden authentication/routing handlers.

## Fidelity and limits

Only the SDK's routed `model` and method `stream` fields are removed from the body; the full resource selects the URL. Original `contents`, tools/history, caller-provided call IDs, thought signatures, media/file references, generation/schema/thinking settings, safety settings, cached-content references and unknown JSON remain untouched. No files are downloaded, tools executed, signatures recreated, IDs stripped or cached history replayed. Complete responses retain candidates, native finish/block reasons, grounding, safety, usage, modelVersion, exact numbers and future fields. Metadata exposes the configured complete resource separately from the upstream's reported model version.

Wire preservation does not establish upstream acceptance of every forwarded field. In particular, Developer API and Vertex function identity/schema dialects can differ. The provider does not silently remove function IDs or downgrade schemas to make a request pass; the selected upstream may reject unsupported fields. Model-specific capability and answer quality require separate live evidence.

Input is snapshotted before the authentication await. Results wait for normal EOF, matching declared length, successful response/client disposal and the final cancellation check. `MaximumBodyBytes` defaults to 32 MiB and bounds raw complete/error bodies; hitting it fails without truncation. Duplicate JSON members, invalid UTF-8/Unicode escapes, truncated JSON, non-object results and SSE media are rejected. HTTP failures retain actual status/raw body; a root native error at HTTP 200 remains an upstream exception with that status. Prompt-block objects remain native without an invented candidate or successful answer.

Explicit cross-protocol conversion is registered through the separate `ConvertingLlmProvider`, fixed to upstream `LlmProtocol.Gemini`. Existing strict field/loss rules remain; a common-subset text integration test does not establish Vertex-specific tool/schema conversion equivalence. Native streaming currently throws `NotSupportedException` and is not simulated from complete JSON.

## Verification status

80 offline xUnit v3 cases compare independent full caller/wire/result fixtures and verify both resource forms, optional quota identity, token rotation and Session recovery, changed resource/principal/configuration rejection, cancellation/expiry, default-header isolation, strict byte/length/JSON boundaries, EOF/disposal failures and explicit text conversion integration. They use substitute HTTP, a deterministic clock and `MemoryDistributedCache` with explicit coordination. The fixtures intentionally include opaque fields and caller-provided tool IDs to test preservation, not to claim live Vertex acceptance. No Google login, actual access-token refresh, real model call or new GitHub Actions run is claimed.

Primary references: [generateContent resource contract](https://cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/projects.locations.publishers.models/generateContent), [native inference](https://cloud.google.com/vertex-ai/generative-ai/docs/model-reference/inference). The roadmap separately tracks SSE, Claude, Express and other cloud dialects.
