# Asgard.Loki.LLM.GoogleVertex

Complete-target `AllowedOutputFormats` declarations apply independently to native Gemini, Claude Messages and MaaS Chat SDK calls. Gemini uses its native MIME/Schema fields, Claude uses `output_config.format`, and MaaS Chat uses `response_format`; deployed model/resource support remains caller-declared. SDK complete/streaming calls reject disallowed formats before adapter authentication or HTTP without Schema conversion or protocol fallback. See the [format matrix and declaration semantics](../../docs/llm-output-formats.md).

Native Vertex Gemini, Claude and MaaS Chat calls for the existing LLM capability. This independent supplier package references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not depend on the Gemini Developer API supplier, Anthropic supplier, Azure, Conversion, a cloud identity SDK or another capability.

## Support matrix

| Operation | Status |
| --- | --- |
| Standard Vertex `generateContent`, complete JSON | Implemented |
| Publisher-model resource form | Explicit `projects/{project}/locations/{location}/publishers/{publisher}/models/{model}`; Google Gemini fixtures |
| Tuned endpoint resource form | Explicit `projects/{project}/locations/{location}/endpoints/{endpoint}`; complete native Gemini JSON |
| External Bearer authentication (standard provider) | Borrowed application token source with stable principal/configuration |
| `streamGenerateContent` SSE | Native events with candidate/block lifecycle and delayed terminal tails |
| Express Gemini JSON/SSE, API Key / short resource root | `GoogleVertexExpressLlmProvider`; explicit separate implementation in this package |
| Claude Messages `rawPredict` JSON / `streamRawPredict` SSE | Implemented; explicit Anthropic resource/body version and native block lifecycle |
| MaaS Chat complete JSON / native SSE | `GoogleVertexMaaSLlmProvider`; explicit project/location endpoint and publisher/model, borrowed External token source |
| Live / Interactions / async batch / cloud resource management | Not implemented |

For the standard provider, the target's `BaseAddress` is an explicit versioned API root, such as `https://us-central1-aiplatform.googleapis.com/v1/` or a caller-owned proxy root. `UpstreamModel` is the **complete raw resource name**, including project and location. Each component is escaped separately. The provider does not infer a region/hostname, normalize a model suffix, map marketing names or choose another native API based on publisher/model text. A configured resource must support the explicitly selected Gemini or Claude operation; resource syntax alone is not proof of model availability.

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

The provider calls the source once per explicit generation, rejects missing/mismatched/expired results, checks source identity before and after acquisition, and checks expiry again immediately before sending. Source configuration drift requires a newly registered provider; its binding identity invalidates previous Sessions. Expired tokens and HTTP authentication failures do not cause refresh retries, request replay or target switching. Access tokens are absent from body, Session fingerprints and default token JSON/display. The standard provider rejects API Key and anonymous credential kinds; Express explicitly supports API Key as described below.

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

The new SSE semantics/resource policies change the provider binding identity from the preceding JSON-only contract. Those Sessions require fresh IDs or caller-controlled expiry/removal, rather than silently rebinding. Existing core credential fingerprints and conversion-v24 remain unchanged. Real cloud/model compatibility and Vertex-specific cross-protocol tool/schema equivalence still require separate evidence.

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

Claude posts native Messages JSON to `:rawPredict`. Only routed `model` is removed, method `stream=false/true` is selected, and `anthropic_version` is supplied in the body. `ClaudeAnthropicVersion` defaults through omission to `vertex-2023-10-16`; an explicitly supplied body version must match the selected option/default, including type and null handling. Conflicts fail before authentication instead of silently replacing caller intent. No `anthropic-version` or first-party Key header is added; inherited defaults containing those fields fail before HTTP dispatch. Future nondefault versions may be configured explicitly but require separate upstream acceptance evidence.

Native system/cache blocks, tools and error-result associations, images, thinking signatures/redacted items, output configuration/schema, usage and unknown fields remain unchanged. No IDs, signatures or schema constraints are removed to obtain a successful response. Native HTTP/root errors keep actual status/raw body. Results use the same strict bounded JSON, EOF, length, cancellation and response/client disposal checks as Gemini. The factory, token source and clock remain borrowed; no retry, fallback or implicit history replay is added.

The nullable version option is omitted from default binding serialization, preserving the preceding Gemini provider identity byte-for-byte. Explicit version configuration participates in the provider fingerprint and invalidates an old Session when changed. A Claude Session cannot switch to Gemini or another principal/resource. Claude JSON's addition does not change core protocols, credential fingerprint algorithms or conversion-v24. Explicit conversion still requires a separate converter fixed to upstream `LlmProtocol.ClaudeMessages`; common-text integration does not grant cross-protocol equivalence for signed/opaque fields. Native Claude SSE is implemented as described below.

## Native Claude SSE

`loki.LLM.ClaudeMessages.StreamAsync` uses the same explicit Claude target and body-version policy, posting `stream=true` to `:streamRawPredict`. A direct adapter call uses `IClaudeMessagesAdapter` to distinguish its event type from Gemini. Original SSE names/IDs/retry hints and native JSON remain unchanged, including thinking/signature deltas, opaque/redacted blocks, tool IDs and partial JSON strings. The provider does not assemble arguments, execute tools, reconstruct signatures or recalculate cumulative usage.

Provider-local state checks message start, original sparse block indices, unique known tool IDs, open/close association and compatible known deltas. Unknown block/delta/event fields remain native. Missing/reopened/duplicate block references, conflicting event types or stop reasons, and message stop before block closure fail. Same-reason `message_delta` cumulative usage updates remain distinct original events. The first native stop reason starts a finite tail buffer; message deltas, stop and later ping/unknown metadata retain their order and wait for normal EOF, declared-length checks, successful transport disposal and final cancellation. No stop reason becomes an invented `end_turn`.

A native `error` before a possible success remains the original failed-terminal event, including when no message started. It waits for EOF/disposal without a fabricated `message_stop`. Native/platform errors after a possible success instead fail the operation and prevent held success events from escaping. HTTP/platform root errors retain original body and actual status, including HTTP 200; a platform error does not need an Anthropic `type` field. Read, cancellation or disposal failures never cause completion, request replay, token retry or reconnection. Early consumer exit releases owned resources while keeping the factory/source borrowed.

`MaximumBodyBytes` bounds the total raw operation before SSE line parsing; `MaximumBufferedTerminalEvents` also bounds Claude tails. Optional `MaximumTrackedClaudeBlocks` defaults through omission to 128 and limits total seen block associations without allocating by index. Explicit zero/negative values fail construction. Omitted Claude block policy preserves preceding Gemini/Claude JSON binding bytes; an explicit changed policy is fingerprinted and invalidates an existing Session. Core protocols, credential fingerprint algorithms and conversion-v24 remain unchanged. Provider-local Claude/Gemini state is independent; Sessions cannot switch protocol or target. The explicit converter keeps its existing field/loss boundaries; native signed/opaque preservation does not imply cross-protocol equivalence.

## Explicit Express Gemini JSON/SSE

`GoogleVertexExpressLlmProvider` is a separate `IGeminiAdapter` in this package. It accepts only API Key credentials and has no token-source/clock dependency, Claude adapter, account management or billing service. Standard `GoogleVertexLlmProvider` keeps its existing External-only contract and binding identity; installing Express adds no core protocol or package dependency.

```csharp
var express = new GoogleVertexExpressLlmProvider(httpFactory);
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [express], Credentials = [new LlmCredential("express-key", apiKey)],
    SessionStore = sessionStore,
    Routes = [new LlmRoute
    {
        Model = "express-model",
        Targets = [new LlmTarget
        {
            Id = "express-target", ProviderId = express.Id,
            BaseAddress = new Uri("https://aiplatform.googleapis.com/v1/"),
            UpstreamModel = "publishers/google/models/your-supported-model",
            CredentialId = "express-key", Protocols = LlmProtocol.Gemini
        }]
    }]
}).Build();
// Call loki.LLM.Gemini.GenerateAsync / StreamAsync with Model = "express-model".
```

Provide the explicit versioned global API root (or a caller-owned proxy) and a raw `publishers/google/models/{model}` resource. Components are escaped, proxy prefixes retained and no project/location is inferred or removed. Complete calls use `:generateContent`; native SSE uses `:streamGenerateContent?alt=sse`. Key material is placed only in `x-goog-api-key`, never automatically in body or URL. A selected request Key overrides same-kind factory defaults; unselected Authorization, other Key or quota-project defaults fail before dispatch. Key whitespace/control characters fail without echoing key material. Configure the named client `Asgard.Loki.LLM.GoogleVertex.Express` with suitable timeouts and no generation retry/hedging or hidden authentication handlers.

Only routed model/method stream fields are removed from Gemini's body. Native tools/IDs, thinking/schema settings, media, safety, unknown fields, exact numbers and results remain unchanged. Field forwarding is not evidence that a selected Express model accepts every field. There is no first-party/standard-mode fallback, automatic login, token acquisition, model-name heuristic, file download, tool execution or history replay. Express account/model/feature availability remains caller-owned.

Complete JSON and HTTP/root errors retain the same strict UTF-8/JSON/length and EOF/disposal semantics. Native Gemini SSE reuses this supplier's private candidate/block-reason state and bounded reader, retaining signatures, original indices, unknown events and original usage. Terminals and all later events wait for normal EOF/length/disposal and final cancellation. `GoogleVertexExpressLlmOptions` independently snapshots total raw bytes (32 MiB), terminal-tail records (128) and tracked candidates (128); limits fail instead of trimming. Factories are borrowed and operation clients/messages/responses are disposed. No Key retry, replay or reconnection is added.

Sessions bind the Express implementation, API root/version, complete short resource, credential ID/Key and resource policy. Key/resource/server/configuration changes reject an old Session instead of reselecting a target. Results expose the configured resource separately from native modelVersion. Explicit cross-protocol conversion requires a converter fixed to upstream Gemini; API Key support is delegated without widening credential kinds. Complete/streamed text integration tests do not claim cross-protocol tool/schema equivalence or live Express acceptance.

## Explicit MaaS Chat JSON/SSE

Instantiate `GoogleVertexMaaSLlmProvider` in this same package, borrowing the existing `IGoogleVertexLlmTokenSource`. MaaS uses a distinct native Chat endpoint; neither the standard Gemini/Claude provider nor Express silently selects it based on a model suffix. Example initialization:

```csharp
var provider = new GoogleVertexMaaSLlmProvider(httpFactory, vertexTokenSource);
await using var client = new LokiClientBuilder().AddLLM(new()
{
    Providers = [provider],
    Credentials = [LlmCredential.External("vertex-principal", "stable-principal")],
    SessionStore = sessionStore,
    Routes = [new()
    {
        Model = "public-model",
        Targets = [new()
        {
            Id = "vertex-maas-target", ProviderId = provider.Id,
            CredentialId = "vertex-principal", Protocols = LlmProtocol.ChatCompletions,
            BaseAddress = new Uri("https://aiplatform.googleapis.com/v1/projects/YOUR_PROJECT/locations/global/endpoints/openapi/"),
            UpstreamModel = "deepseek-ai/YOUR_ENABLED_MODEL"
        }]
    }]
}).Build();
var result = await client.LLM.ChatCompletions.CompleteAsync(new()
{
    Model = "public-model",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new() { SessionId = "maas-conversation" });
```

The configured base explicitly ends in `projects/{project}/locations/{location}/endpoints/openapi`; regional/global hosts and proxy prefixes are caller choices. `UpstreamModel` is the exact `publisher/model` body selector, without inferred `-maas` suffixes or a resource-path rewrite. The provider posts to `chat/completions`, replaces only routed `model` and method `stream=false/true`, and retains all other native fields. Returned native model and result fields stay unchanged, while metadata retains the configured target model. Preservation of thinking/tool/schema/extensions is a wire guarantee, not acceptance by every MaaS model.

Only External credentials are accepted. Stable source identity, principal and full target/configuration bind Sessions; ordinary short-lived Bearer rotation does not change the binding. Acquisition is once per generation with source/principal/expiry checks and another expiry check before send. Selected Bearer/quota headers override same-kind defaults; unselected Key/project headers fail. No login, token discovery, refresh retry, regional failover, subscription or model management is implemented. The named client defaults to `Asgard.Loki.LLM.GoogleVertex.MaaS`. `GoogleVertexMaaSLlmOptions` snapshots a 32 MiB raw-byte limit (including the whole SSE), optional explicit quota-project header and independent terminal-tail/choice/tool count limits (128 each); changed policy invalidates previous MaaS Sessions.

Complete JSON/root errors retain original status/body; strict UTF-8, duplicate keys, exact raw-byte/Content-Length, EOF, disposal and final cancellation guard successful publication. Native SSE preserves complete JSON chunks, original SSE event/ID/retry, tool indices/IDs, fragmented names/arguments, reasoning and unknown native fields without aggregation. Exact nonnegative numeric choice/tool indices remain raw; sparse values never allocate an index-sized array. Choice/tool and returned id/model associations are checked; a changed/reused tool ID, missing ID at finish, duplicate/reopened/missing choice finish or missing DONE fails. All events from the first choice finish, including usage and unknown tails, are bounded and held until DONE plus EOF/length, response/client disposal and final cancellation. Events after DONE fail; a comment can follow. Unknown native finish reasons remain native. These SSE semantics use a new provider binding domain; Sessions created with the preceding JSON-only MaaS implementation must be recreated. Core credential/conversion and standard/Express provider identities are unchanged. No real MaaS account, login or model call was used. See the [official MaaS API contract](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/maas/call-open-model-apis).

## Verification status

Tests (248 standard Vertex, 67 Express, 74 MaaS JSON and tests) compare independent full caller/wire/result fixtures and verify both resource forms, optional quota identity, token rotation and Session recovery, changed resource/principal/configuration rejection, cancellation/expiry, default-header isolation, strict byte/length/JSON boundaries, EOF/disposal failures and explicit complete/streamed text conversion integration. Independent raw SSE and full event-sequence goldens additionally verify native function IDs/arguments, thought signatures, grounding, unknown objects, exact integers, original event identity/retry and usage tails. Sparse/default indices, ProtoJSON numeric forms, native finish/block reasons, truncation, duplicates, late events/errors, limits, length/encoding, cancellation and read/disposal failures have explicit tests. They use substitute HTTP, a deterministic clock and `MemoryDistributedCache` with explicit coordination. Tests compare independent complete request/wire/result goldens and cover version agreement/conflict, explicit resource/header boundaries, preserved Gemini identity, refreshed-token Session recovery, changed principal/version/protocol rejection, text conversion and late transport failures. The additional tests use independent raw SSE/full event-sequence goldens and cover message/block/tool association, thinking/signature and JSON deltas, cumulative usage/unknown tails, native error versus late errors, truncation/duplicates/changed reasons, limits, byte/length/encoding, cancellation/disposal, snapshots, Session recovery and explicit common-text conversion streams. The fixtures intentionally include opaque fields and caller-provided tool IDs to test preservation, not to claim live Vertex acceptance. No Google login, actual access-token refresh, real model call or new GitHub Actions run is claimed.

Primary references: [Express API resource/authentication contract](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/express-mode/api-reference), [Claude native SSE](https://platform.claude.com/docs/en/build-with-claude/streaming), [Claude on Google Cloud](https://platform.claude.com/docs/en/build-with-claude/claude-on-vertex-ai), [generateContent resource contract](https://cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/projects.locations.publishers.models/generateContent), [native inference](https://cloud.google.com/vertex-ai/generative-ai/docs/model-reference/inference), [response fields and native finish reasons](https://cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/GenerateContentResponse), [ProtoJSON numeric representations](https://protobuf.dev/programming-guides/json/). The roadmap separately tracks project-scoped API-Key, Bedrock and other cloud dialects.

Express has tests: three complete caller/wire/result fixtures plus shared native Gemini raw SSE/full-event goldens. They verify v1/v1beta1 roots, headers, snapshots, field retention, target/Key/configuration affinity, default-header isolation, bounds/faults/cancellation and explicit complete/streamed text conversion. Standard Vertex regressions remain separate; no real Express Key or model call was used.

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.
