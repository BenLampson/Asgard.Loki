# Asgard.Loki.Image.GoogleVertex

Native resource-scoped Vertex Gemini image generation, conversational image inputs and original SSE. Independent Image provider, depending only on Image, Infrastructure and standard HTTP factory support.

| Operation/profile | Native resource and authentication | Status |
| --- | --- | --- |
| Complete JSON and original SSE | projects/{project}/locations/{location}/publishers/google/models/{model}; explicit Bearer source | Offline vectors/tests |
| Complete JSON and original SSE | Same full project resource; explicit x-goog-api-key | Offline vectors/tests; caller must provide an authorized key |
| Express JSON and original SSE | publishers/google/models/{model}; explicit x-goog-api-key | Offline vectors/tests; model must support Express/image operation |
| Old Imagen3/4 predict endpoints | Retired 2026-06-30 | Excluded, official status checked 2026-10-20 |

## Initialization

```csharp
var adapter = new GoogleVertexImageAdapter(httpClientFactory, applicationTokenSource, new()
{
    Id = "vertex-images", ModelResource = configuredFullGoogleModelResource,
    BaseAddress = configuredVersionRoot, PrincipalIdentity = configuredPrincipal,
    QuotaProjectId = configuredQuotaProject // optional; never inferred
});
// API-Key constructor explicitly selects project-scoped or Express key authentication:
var express = new GoogleVertexImageAdapter(httpClientFactory, new GoogleVertexImageOptions
{
    ModelResource = "publishers/google/models/" + configuredModelId,
    BaseAddress = new("https://aiplatform.googleapis.com/v1/"), ApiKey = configuredImageKey
});
var result = await adapter.GenerateNativeAsync(new()
{
    PublicModel = "pictures",
    Parameters = JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"a cat"}]}],"generationConfig":{"responseModalities":["TEXT","IMAGE"]}}""")!.AsObject()
}, cancellationToken);
var bytes = result.Images[0].Decode(); // explicit; no media fetching/writing
```

The application's `IGoogleVertexImageTokenSource` provides a stable BindingIdentity and one immutable GoogleVertexImageAccessToken with exact principal assertion and expiry per operation. The SDK borrows this source, the HTTP factory and optional TimeProvider. Authentication discovery, login, ADC files, token refreshing and principal authorization belong to the application. Source/configuration drift, wrong principal and expiry before sending fail locally. Same-principal token rotation is supported; token expiry after an authorized request does not invalidate its completed response. Key and token constructor options cannot be mixed. No automatic credential fallback or URL key query parameters. Unbound default authentication/quota headers are rejected before I/O.

## Native contract and limits

Methods bind explicit HTTPS version/root and Google publisher resource; no region/model/version inference or latest model catalog. Existing AddImage generation routes support common prompts with native root settings. Direct GenerateNativeAsync/StreamNativeAsync accept complete original contents/history, inlineData/fileData image references, signatures, tools and generationConfig. Direct PublicModel labels metadata only and does not select a route. Body model and stream overrides are rejected; native methods are generateContent and streamGenerateContent?alt=sse. No implicit output modality/size changes, token estimates or automatic tool execution.

Complete Data and every original SSE JSON/name/id/retry remain unchanged. Images is a convenience view over original candidate image parts, retaining signatures and original item fields; Decode is explicit. Safety blocks, NO_IMAGE and candidate safety finish reasons remain native outcomes with zero images. HTTP/API errors preserve original body/status/content type; SSE errors also retain event identity. Native default candidate index0 may be omitted. Every observed candidate must terminate or a native prompt block must occur; unspecified finish reasons do not finish. Candidate continuation/repeated finish is invalid. Terminal frames and following usage wait for complete EOF, byte/declared-length checks, source revalidation and owned resource disposal. No fake completions, image chunk concatenation, JSON-to-SSE fallback, stream replay or reconnect.

Configurable request and total response/SSE byte limits apply before decoding. Per-call messages, responses and clients are disposed, while borrowed dependencies stay application-owned. Disable handler retries, hedging and redirects. No sessions, media download, file management, cloud project/IAM setup, background workers, billing, LLM/Video dependencies or generic gateway services.

Live status: **not verified with real Vertex image credentials**. Tests use independent full native vectors and fake HTTP/auth/time only. [Cloud REST auth](https://docs.cloud.google.com/docs/authentication/rest), [Express endpoints/key header](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/start/express-mode/overview), [retired Imagen endpoints](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/imagen/4-0-generate). Protocol coverage does not claim every model/version/key supports an operation. The separate Video providers retain their configured preview/stable calls; Imagen retirement does not alter them.
