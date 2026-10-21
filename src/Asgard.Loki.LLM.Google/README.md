# Asgard.Loki.LLM.Google

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

Use a **bare model ID**, without a `models/` prefix, in `UpstreamModel`. The adapter encodes it as a path segment. The body contains native Gemini fields; `Model` is mapped into the URL and the SDK's method-selected `stream` flag is omitted from the body. Authentication uses request-local `x-goog-api-key`, never a key in the URL. The named client is `Asgard.Loki.LLM.Google`; disable unsafe retries and hedging.

Results preserve the original `modelVersion` and `responseId` rather than adding artificial OpenAI fields. Use `result.Data` for these native properties. Session routing metadata is available separately. The caller supplies and owns cache, coordinator and HTTP factory. No tool execution or automatic history reconstruction is performed.

This provider references only its LLM capability and standard Microsoft HTTP components. Optional protocol conversion lives in a separate package. Offline xUnit v3 tests verify final wire payloads, opaque content, fragmented SSE, errors and Session binding. Gemini has **not been live-verified**. See [generateContent](https://ai.google.dev/api/generate-content) and [function calling](https://ai.google.dev/gemini-api/docs/function-calling).

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

Complete root/model/Key/provider/client/version and these policies participate in Session affinity. The native HTTP implementation now uses its own versioned binding identity; prior weaker transport bindings cannot silently resume under the changed contract. The three-argument constructor and explicitly supplied default options produce the same current binding. Configure a fresh Session when changing implementation/client/policies. The named client cannot carry unselected Authorization/x-api-key/api-key or AWS signature defaults; request-local x-goog-api-key and Host belong to the selected target. Borrowed factory/cache/coordinator ownership is unchanged, and native parameters/model capabilities remain caller-selected.

91 passing offline xUnit v3 cases cover independent complete New API/Sub2API/proxy native Gemini windows, original thought signatures, reverse function results, inline/file references, schema/thinking/unknown settings, exact usage, raw error bytes and the full resource/Session boundaries. These are protocol checks using fake HTTP and MemoryDistributedCache with explicit test coordination. No real Gemini/proxy/model, option effect, ciphertext validity or generated quality has been tested. Standard provider packages are reused by compatible native upstreams; this adds no relay service or provider-peer dependency.
