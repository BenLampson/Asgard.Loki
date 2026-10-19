# Asgard.Loki.LLM.GoogleVertex

Native Vertex Gemini and Claude calls for the existing LLM capability. This independent supplier package references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not depend on the Gemini Developer API supplier, Anthropic supplier, Azure, Conversion, a cloud identity SDK or another capability.

## Support matrix

| Operation | Status |
| --- | --- |
| Standard Vertex `generateContent`, complete JSON | Implemented |
| Publisher-model resource form | Explicit `projects/{project}/locations/{location}/publishers/{publisher}/models/{model}`; Google Gemini fixtures |
| Tuned endpoint resource form | Explicit `projects/{project}/locations/{location}/endpoints/{endpoint}`; complete native Gemini JSON |
| External Bearer authentication | Borrowed application token source with stable principal/configuration |
| `streamGenerateContent` SSE | Native events with candidate/block lifecycle and delayed terminal tails |
| Express-mode API Key / short resource root | Not implemented by this provider |
| Claude Messages `rawPredict`, complete JSON | Implemented; explicit Anthropic publisher resource and body version |
| Claude `streamRawPredict`, OpenAI-compatible MaaS | Pending / not implemented |
| Live / Interactions / async batch / cloud resource management | Not implemented |

The target's `BaseAddress` is an explicit versioned API root, such as `https://us-central1-aiplatform.googleapis.com/v1/` or a caller-owned proxy root. `UpstreamModel` is the **complete raw resource name**, including project and location. Each component is escaped separately. The provider does not infer a region/hostname, normalize a model suffix, map marketing names or choose another native API based on publisher/model text. A configured resource must support the explicitly selected Gemini or Claude operation; resource syntax alone is not proof of model availability.

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

For Gemini, only the SDK's routed `model` and method `stream` fields are removed from the body; the full resource selects the URL. Original `contents`, tools/history, caller-provided call IDs, thought signatures, media/file references, generation/schema/thinking settings, safety settings, cached-content references and unknown JSON remain untouched. No files are downloaded, tools executed, signatures recreated, IDs stripped or cached history replayed. Complete responses retain candidates, native finish/block reasons, grounding, safety, usage, modelVersion, exact numbers and future fields. Metadata exposes the configured complete resource separately from the upstream's reported model version.

Wire preservation does not establish upstream acceptance of every forwarded field. In particular, Developer API and Vertex function identity/schema dialects can differ. The provider does not silently remove function IDs or downgrade schemas to make a request pass; the selected upstream may reject unsupported fields. Model-specific capability and answer quality require separate live evidence.

Input is snapshotted before the authentication await. Results wait for normal EOF, matching declared length, successful response/client disposal and the final cancellation check. `MaximumBodyBytes` defaults to 32 MiB and bounds raw complete/error/SSE bodies (the total raw operation bytes, including SSE comments and metadata); hitting it fails without truncation. Duplicate JSON members, invalid UTF-8/Unicode escapes, truncated JSON, non-object results and SSE media are rejected. HTTP failures retain actual status/raw body; a root native error at HTTP 200 remains an upstream exception with that status. Prompt-block objects remain native without an invented candidate or successful answer.

Explicit cross-protocol conversion is registered through the separate `ConvertingLlmProvider`, fixed to upstream `LlmProtocol.Gemini`. Existing strict field/loss rules remain; a common-subset text integration test does not establish Vertex-specific tool/schema conversion equivalence. Native streaming uses the same fixed Vertex target and borrowed authentication through `Gemini.StreamAsync`. It does not simulate events from complete JSON. Native numeric-enum transport support does not expand the documented conversion subset.

## Native Gemini SSE lifecycle

`Gemini.StreamAsync` posts the unchanged native body to the complete configured resource's `:streamGenerateContent?alt=sse` method. Before the first possible terminal, records remain incremental. The first finished candidate or native prompt block starts a finite tail buffer; all subsequent records retain their original order through normal EOF, declared-length checks and successful response/client disposal. Usage, signatures, grounding and unknown metadata are neither dropped nor reconstructed. Missing/repeated finishes, reopened candidates, changed response/model-version identity, malformed/truncated events or late native errors fail without releasing held terminals.

Native `STOP`, `MAX_TOKENS`, safety and future finish reasons stay native; no reason becomes a fabricated successful stop. A prompt-block object remains a block without an invented candidate. Candidate indices are not reordered or rewritten. An omitted index associates with the native default zero; protobuf-compatible quoted/exponent integer forms are read exactly without float rounding. Enum names and int32 numeric forms are retained; zero/unspecified does not constitute a terminal. No candidate content, tool arguments, thought signature or token count is aggregated or interpreted by this provider.

`MaximumBodyBytes` defaults to 32 MiB and bounds the total raw operation before SSE line parsing. `MaximumBufferedTerminalEvents` and `MaximumTrackedCandidates` each default to 128. Exceeding a bound fails instead of trimming records or changing association. Raw SSE event names, IDs and retry hints remain metadata; no reconnect, retry, automatic continuation or request replay occurs. Early consumer exit disposes owned resources without generating completion. This native protocol ends at EOF after all observed candidates finish, or after a native prompt block; Chat's `[DONE]` is not a substitute for Vertex's native lifecycle.

The new SSE semantics/resource policies change the provider binding identity from the preceding JSON-only checkpoint. Those Sessions require fresh IDs or caller-controlled expiry/removal, rather than silently rebinding. Existing core credential fingerprints and conversion-v24 remain unchanged. Real cloud/model compatibility and Vertex-specific cross-protocol tool/schema equivalence still require separate evidence.

## Native Claude complete JSON

Use the same `GoogleVertexLlmProvider`, External credential and coordinated Session store. Configure the target with `Protocols = LlmProtocol.ClaudeMessages` and a full `projects/{project}/locations/{location}/publishers/anthropic/models/{model}` resource. The actual Vertex model ID is caller-owned, including any `@revision`; the SDK escapes it without mapping a first-party model name. Gemini resources and tuned endpoints are rejected for this Claude operation before token acquisition. Selecting Claude never depends on a model-name prefix.

```csharp
var message = await loki.LLM.ClaudeMessages.CreateAsync(new ClaudeMessageRequest
{
    Model = "your-claude-public-alias", MaxTokens = 512,
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
}, new LlmCallOptions { SessionId = "claude-session" }, cancellationToken);
// This alias must route to the explicit Claude target described above.
```

Claude posts native Messages JSON to `:rawPredict`. Only routed `model` is removed, method `stream=false` is selected, and `anthropic_version` is supplied in the body. `ClaudeAnthropicVersion` defaults through omission to `vertex-2023-10-16`; an explicitly supplied body version must match the selected option/default, including type and null handling. Conflicts fail before authentication instead of silently replacing caller intent. No `anthropic-version` or first-party Key header is added; inherited defaults containing those fields fail before HTTP dispatch. Future nondefault versions may be configured explicitly but require separate upstream acceptance evidence.

Native system/cache blocks, tools and error-result associations, images, thinking signatures/redacted items, output configuration/schema, usage and unknown fields remain unchanged. No IDs, signatures or schema constraints are removed to obtain a successful response. Native HTTP/root errors keep actual status/raw body. Results use the same strict bounded JSON, EOF, length, cancellation and response/client disposal checks as Gemini. The factory, token source and clock remain borrowed; no retry, fallback or implicit history replay is added.

The nullable version option is omitted from default binding serialization, preserving the preceding Gemini provider identity byte-for-byte. Explicit version configuration participates in the provider fingerprint and invalidates an old Session when changed. A Claude Session cannot switch to Gemini or another principal/resource. Claude JSON's addition does not change core protocols, credential fingerprint algorithms or conversion-v24. Explicit conversion still requires a separate converter fixed to upstream `LlmProtocol.ClaudeMessages`; common-text integration does not grant cross-protocol equivalence for signed/opaque fields. Claude SSE remains pending and currently throws `NotSupportedException` through `IClaudeMessagesAdapter`.

## Verification status

190 offline xUnit v3 cases (80 original Gemini JSON/authentication, 70 Gemini SSE/configuration and 40 Claude JSON cases) compare independent full caller/wire/result fixtures and verify both resource forms, optional quota identity, token rotation and Session recovery, changed resource/principal/configuration rejection, cancellation/expiry, default-header isolation, strict byte/length/JSON boundaries, EOF/disposal failures and explicit complete/streamed text conversion integration. Independent raw SSE and full event-sequence goldens additionally verify native function IDs/arguments, thought signatures, grounding, unknown objects, exact integers, original event identity/retry and usage tails. Sparse/default indices, ProtoJSON numeric forms, native finish/block reasons, truncation, duplicates, late events/errors, limits, length/encoding, cancellation and read/disposal failures have explicit tests. They use substitute HTTP, a deterministic clock and `MemoryDistributedCache` with explicit coordination. Forty additional Claude JSON cases compare independent complete request/wire/result goldens and cover version agreement/conflict, explicit resource/header boundaries, preserved Gemini identity, refreshed-token Session recovery, changed principal/version/protocol rejection, text conversion and late transport failures. Claude SSE is not claimed by these JSON tests. The fixtures intentionally include opaque fields and caller-provided tool IDs to test preservation, not to claim live Vertex acceptance. No Google login, actual access-token refresh, real model call or new GitHub Actions run is claimed.

Primary references: [Claude on Google Cloud](https://platform.claude.com/docs/en/build-with-claude/claude-on-vertex-ai), [generateContent resource contract](https://cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/projects.locations.publishers.models/generateContent), [native inference](https://cloud.google.com/vertex-ai/generative-ai/docs/model-reference/inference), [response fields and native finish reasons](https://cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/GenerateContentResponse), [ProtoJSON numeric representations](https://protobuf.dev/programming-guides/json/). The roadmap separately tracks Claude SSE, Express and other cloud dialects.
