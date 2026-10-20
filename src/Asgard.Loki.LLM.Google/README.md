# Asgard.Loki.LLM.Google

Native Gemini Developer API provider for the Asgard.Loki .NET SDK.

| API / feature | Support |
| --- | --- |
| `models/{model}:generateContent` | Complete JSON |
| `models/{model}:streamGenerateContent?alt=sse` | SSE |
| `systemInstruction`, tools/results, inline/file media, generationConfig, thought signatures and future fields | Preserved in their native shape |
| Unknown SSE data and metadata | Preserved |
| HTTP errors / interrupted streams | Original status/body; every observed candidate needs a finish reason, or an explicit block/error |
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
