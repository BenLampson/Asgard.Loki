# Asgard.Loki.Image.Google

Native Gemini Developer API image generation and conversational editing inputs in an independent Image provider. This package references Image, Infrastructure and HTTP factory support only.

| Operation | Native interface | Verification |
| --- | --- | --- |
| Complete native image/text result | POST models/{configured model}:generateContent | Offline protocol vectors |
| Original native stream | POST models/{configured model}:streamGenerateContent?alt=sse | Offline protocol vectors; requires a model supporting image streaming |
| Imagen predict | Excluded: retired in Gemini API | Official retirement checked 2026-10-20 |

## Initialize

```csharp
var adapter = new GoogleImageAdapter(httpClientFactory, new()
{
    Id = "google-image", Model = configuredNativeModelId, ApiKey = imageApiKey,
    BaseAddress = configuredVersionRoot // e.g. https://generativelanguage.googleapis.com/v1beta/
});
var result = await adapter.GenerateNativeAsync(new()
{
    PublicModel = "pictures",
    Parameters = JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"a cat"}]}],"generationConfig":{"responseModalities":["TEXT","IMAGE"]}}""")!.AsObject()
}, cancellationToken);
var bytes = result.Images[0].Decode(); // explicit; SDK never writes or fetches media
```

Use existing `AddImage` routes with `GoogleImageAdapter` for common prompt requests. Native root settings such as generationConfig go into common request Parameters; contents cannot override the prompt. Direct `GenerateNativeAsync` / `StreamNativeAsync` accept full native contents/history, image references, inlineData, fileData, signatures, tools, imageConfig and extensions. Direct PublicModel labels results only; no public routing occurs. Actual model ID is explicit and placed in the path; body model and stream overrides fail locally. No implicit model catalog, method fallback, output-modality defaults or parameter conversions.

## Preserved results and limitations

`Data` is the complete native JSON. `Images` projects image MIME inline payloads from native candidate parts, retaining each complete original part with signatures and field names. Both native inlineData/mimeType and inline_data/mime_type spellings are supported without rewriting JSON. Decode is explicit. Text-only results, prompt safety blocks, candidate IMAGE_SAFETY/NO_IMAGE and other native finish reasons remain accessible outcomes, including zero images. Only HTTP/API error envelopes raise ImageUpstreamException with original body/status/content type; SSE errors additionally retain original event identity. Usage and safety metadata are never estimated or dropped.

SSE preserves original text/image/thought/signature chunks, candidate indices, event name/id/retry and trailing usage. Every observed candidate needs a native finish reason, or an explicit native prompt block; unspecified reasons do not complete a call. Candidate continuation/repeated finish after termination fails. Terminal events and trailing metadata wait for full EOF, byte/declared-length verification and owned resource disposal. No synthetic completion, reconstruction/concatenation of images, automatic continuation, reconnect, retries or stream replay. Use a model and API version that support the requested native operation; protocol tests do not establish model availability or image quality.

Configure version root and independent x-goog-api-key explicitly. The SDK borrows the factory and owns per-call messages/clients/responses. Disable retries, hedging and redirects on application handlers. Configurable limits bound request JSON and total response/SSE bytes. No media download, generic file management, sessions, automatic tool execution, backend or billing. This package does not reference LLM, Video or Vertex providers.

Live status: **not tested with real Google image credentials**. Offline tests use independent native JSON/SSE and fake HTTP only. [Generate Content image guide](https://ai.google.dev/gemini-api/docs/generate-content/image-generation), [API reference](https://ai.google.dev/api/generate-content). [Imagen is shut down in the Gemini API](https://ai.google.dev/gemini-api/docs/imagen); no retired predict operation or automatic migration is implemented. Vertex Imagen and newer APIs are outside this package coverage.
