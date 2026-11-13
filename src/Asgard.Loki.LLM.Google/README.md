# Asgard.Loki.LLM.Google

Gemini text-output restrictions can be declared with `LlmTarget.AllowedOutputFormats[LlmProtocol.Gemini]`. SDK complete/streaming calls recognize native legacy MIME/Schema fields and modern `responseFormat.text`; Schema dialects and request fields stay unchanged. See the [format matrix and target configuration](../../docs/llm-output-formats.md). Declare only the selected model's accepted formats; no MIME/default injection or automatic downgrade occurs.

Native Gemini Developer API provider for the Asgard.Loki .NET SDK.

| API / feature | Support |
| --- | --- |
| `models/{model}:generateContent` | Complete JSON |
| `models/{model}:streamGenerateContent?alt=sse` | SSE |
| `systemInstruction`, tools/results, inline/file media, generationConfig, thought signatures and future fields | Preserved in their native shape |
| Unknown SSE data and metadata | Preserved |
| HTTP errors / interrupted streams | Original status/body bytes/media type; candidate endings require normal EOF, owned closure and final cancellation |
| Vertex AI OAuth, Live API, token counting, batch and file-upload APIs | Not implemented |

```csharp
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Google;

var provider = new GoogleLlmProvider(httpFactory);
var target = new LlmTarget
{
    Id = "gemini-primary", ProviderId = provider.Id,
    BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/"),
    UpstreamModel = geminiModel, CredentialId = "gemini-key",
    Protocols = LlmProtocol.Gemini
};
// Register provider, target and new LlmCredential("gemini-key", geminiApiKey)
// using LlmOptions, a route named "assistant" and the caller's SessionStore.
var result = await loki.LLM.Gemini.GenerateAsync(new GeminiContentRequest
{
    Model = "assistant",
    Contents = [new System.Text.Json.Nodes.JsonObject
    {
        ["role"] = "user",
        ["parts"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["text"] = "Hello" })
    }]
}, new LlmCallOptions { SessionId = "gemini-conversation" }, cancellationToken);
```

With the default operation paths, use a **bare model ID**, without a `models/` prefix, in `UpstreamModel`. The adapter encodes it as a path segment. The body contains native Gemini fields; `Model` is mapped into the URL and the SDK's method-selected `stream` flag is omitted from the body. Default authentication uses request-local x-goog-api-key. Other credential locations require the explicit configuration below. The named client is `Asgard.Loki.LLM.Google`; disable unsafe retries and hedging.

Results preserve the original `modelVersion` and `responseId` rather than adding artificial OpenAI fields. Use `result.Data` for these native properties. Session routing metadata is available separately. The caller supplies and owns cache, coordinator and HTTP factory. No tool execution or automatic history reconstruction is performed.

This provider references only its LLM capability, Infrastructure and standard Microsoft HTTP components. Optional protocol conversion lives in a separate package. Offline xUnit v3 tests verify final wire payloads, opaque content, fragmented SSE, errors and Session binding. Gemini has **not been live-verified**. See [generateContent](https://ai.google.dev/api/generate-content) and [function calling](https://ai.google.dev/gemini-api/docs/function-calling).

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.

## Native HTTP and ending policies

The original constructor remains available. Select explicit limits with the options overload:

```csharp
var provider = new GoogleLlmProvider(httpFactory, new GoogleHttpOptions
{
    MaximumRequestBytes = 32 * 1024 * 1024,
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumBufferedTerminalEvents = 128,
    MaximumTrackedCandidates = 128
}, id: "selected-gemini", httpClientName: "Explicit.Gemini.Native");
```

The complete request and raw JSON/error/SSE stream have explicit byte budgets. Declared lengths must be valid and match actual bytes. JSON and SSE reject invalid UTF-8, duplicate properties and invalid escaped Unicode instead of replacing or losing data. Native responseId/modelVersion, when present and nonempty on native frames, must stay consistent. Candidate association is bounded by entry count, supports sparse indices and never allocates by index size. Absent candidate index retains its native default-zero meaning without inserting a field into Data.

The first actual candidate finish or global error/block begins bounded tail buffering. Each observed candidate must finish unless the upstream supplies a native global failure. Candidate endings, usage and following unknown metadata are delivered only after actual EOF, stream/response/client/message closure and final cancellation. There is no synthetic DONE or STOP. Late read/disposal/cancellation failure, missing/duplicate candidate ends and identity drift fail without releasing buffered endings. Unknown metadata-only business type frames remain original data; their error/responseId fields are not native Gemini lifecycle signals.

The first native error/block remains original protocol data after the same closure gate. A later native error/block after a possible normal ending withdraws that ending and throws LlmUpstreamException with actual HTTP status, original error-frame payload bytes and media type. HTTP failures preserve arbitrary binary error bytes; a streaming request receiving non-SSE keeps its original body in the exception. These raw upstream failures propagate through explicit conversion. They do not become successful function responses, citations or converted completions. Early consumer exit disposes only the current call; no background consumption or retry follows.

Complete root/model/Key/provider/client/version and these policies participate in Session affinity. The native HTTP implementation uses its own versioned binding identity; prior weaker transport bindings cannot silently resume under the changed contract. The three-argument constructor and explicitly supplied default options produce the same current binding. Configure a fresh Session when changing implementation/client/policies. The named client cannot carry unselected Authorization/x-api-key/api-key or AWS signature defaults; request-local x-goog-api-key and Host belong to the selected target. Borrowed factory/cache/coordinator ownership is unchanged, and native parameters/model capabilities remain caller-selected.

Tests cover explicit native endpoint/auth profiles and independent complete New API/Sub2API/proxy native Gemini windows, original thought signatures, reverse function results, inline/file references, schema/thinking/unknown settings, exact usage, raw error bytes and the full resource/Session boundaries. These are protocol checks using fake HTTP and MemoryDistributedCache with explicit test coordination. No real Gemini/proxy/model, option effect, ciphertext validity or generated quality has been tested. Standard provider packages are reused by compatible native upstreams; this adds no relay service or provider-peer dependency.

## Explicit native Gemini endpoints and authentication

GoogleHttpOptions adds one explicit Authentication selector and an optional frozen GoogleHttpTransportOptions snapshot. These configure a caller-selected existing native Gemini endpoint; the provider retains native bodies, candidate associations and lifecycle rules. Complete server/root and actual model remain in LlmTarget. No model/server discovery or protocol inference is performed.

| Operation / mode | Support | Limits / live status |
| --- | --- | --- |
| GenerateContentPath / StreamGenerateContentPath | Separate native JSON and SSE operation paths/fixed queries beneath the complete root | Original request/body/tail/candidate budgets apply; no live endpoint/model verification |
| ApiKey (default) | Selected x-goog-api-key header | Ordinary API-Key credential; default options/binding bytes retained |
| Bearer | Selected Authorization: Bearer | Caller-supplied static API-Key identity; no token discovery/refresh |
| Header / Query | Explicit CredentialName, CredentialPrefix + Key + CredentialSuffix | Structural header names/conflicting encoded or case-variant fixed credential parameters rejected |
| None | Explicit LlmCredential.Anonymous | No implicit anonymous mode from a missing Key; external identities remain unsupported here |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Google;

// factory, sessionStore, selectedCompleteRoot, selectedModel and selectedApiKey are caller-owned.
var provider = new GoogleLlmProvider(factory, new GoogleHttpOptions
{
    Authentication = GoogleHttpAuthentication.Query,
    Transport = new GoogleHttpTransportOptions
    {
        GenerateContentPath = "gemini/{model}:generate?existing=1",
        StreamGenerateContentPath = "gemini/{model}:stream?alt=sse&existing=1",
        CredentialName = "api_key"
    }
}, id: "native-gemini", httpClientName: "Explicit.Native.Gemini");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("selected-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "public", Targets = [new LlmTarget
        {
            Id = "selected-native", ProviderId = provider.Id,
            BaseAddress = selectedCompleteRoot, UpstreamModel = selectedModel,
            CredentialId = "selected-key", Protocols = LlmProtocol.Gemini,
            Version = "deployment-context-v1"
        }]
    }]
}).Build();
```

In the normal model-path mode, each operation path must contain exactly one {model} position in its path, with none in its query. The selected actual model is escaped once as one value; literal slashes, percent signs and Unicode are not decoded or repaired. Configuration traversal is rejected separately from escaped model characters. The native body omits model and stream, retaining contents/history/tools/thoughtSignature/generationConfig and unknown fields. Custom streaming paths must explicitly include alt=sse when required by that upstream; the SDK does not repair fixed queries or infer a streaming operation from another path.

For a server operation explicitly dedicated to one model, configure both static paths plus the exact FixedUpstreamModel:

```csharp
var transport = new GoogleHttpTransportOptions
{
    GenerateContentPath = "fixed:generate?existing=1",
    StreamGenerateContentPath = "fixed:stream?alt=sse",
    FixedUpstreamModel = selectedModel
};
```

Fixed paths must omit {model}; FixedUpstreamModel must exactly equal the target's actual model before any client is created. This is a caller declaration about that fixed endpoint, not a live model check. The fixed declaration, operation paths and target model all participate in the complete binding. No model is invented in the body or silently taken from a server default.

Paths append beneath the complete target root/proxy prefix; a single leading slash has the same append semantics. Fixed queries retain their original order/values and repeated business parameters, including literal URL values. Absolute/scheme-relative endpoints, fragments, unknown/repeated templates, traversal, invalid percent/UTF-8 escapes and control characters are rejected. Query credential names and formatted values are escaped exactly once. Header values use printable ASCII. Keep the actual Key in LlmCredential, with only non-secret text in prefix/suffix. ApiKey/Bearer/None reject unused credential formatting; Header/Query require Transport with a name.

To select no authentication, set Authentication = GoogleHttpAuthentication.None and register LlmCredential.Anonymous("selected-key"). Transport can be omitted to retain native model paths. Default ApiKey and null Transport are omitted from serialized options, preserving the earlier four-policy default binding exactly. Explicit Transport opts into stricter wire verification and a distinct binding even with its default paths. Actual server/model/credential, paths/fixed-model declaration/auth formatting, client/resource policies and target Version remain bound; changes reject old Sessions before another send.

Configured requests freeze URI/Host/body/selected credential/Accept before factory creation. Same-name defaults are overridden on the request; unselected authentication/signature/Cookie defaults fail without mutating factory settings. Actual method/URI/Host/auth/content are rechecked across frames and final resource closure. Raw HTTP errors and late native error/block failures preserve original status, media and arbitrary bytes. Native candidates/responseId/modelVersion, unknown business metadata and EOF/owned-closure/final-cancellation gates retain their existing semantics. Early exit closes only this call. Factory/cache/coordinator remain borrowed. Disable automatic retries, redirects, hedging or hidden authentication changes in the application HTTP pipeline; update target Version for identity-affecting pipeline changes under a stable client name.

The existing explicit Conversion decorator delegates credential support and native binding. This package still references only LLM, Infrastructure and standard HTTP support; no supplier-peer dependency, incoming route engine, backend, polling, tool execution or hidden history is added.

Tests compare independently retained literal native windows across five auth modes, model-path/static-endpoint profiles and JSON/SSE. Tests cover exact model/credential escaping, full tool/history/signature/unknown/candidate windows, original error bytes, terminal loss/repetition/post-data/native failures, identity drift, config/credential/default guards, complete Session replay/drift, early exit and late EOF/final cancellation. Server/model/optional-setting/signature acceptance and generated quality remain unverified.
