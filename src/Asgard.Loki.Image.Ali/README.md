# Asgard.Loki.Image.Ali

Native Ali image generation, interleaved text/image SSE, and explicit asynchronous image tasks. Depends only on Image, Infrastructure and standard HTTP factory support.

| Operation | Native interface | Status |
| --- | --- | --- |
| JSON / original SSE | POST services/aigc/multimodal-generation/generation | Offline protocol tests |
| Modern async generation | POST services/aigc/image-generation/generation | Offline protocol tests |
| Legacy text-to-image | POST services/aigc/text2image/image-synthesis | Offline protocol tests |
| Legacy image-to-image | POST services/aigc/image2image/image-synthesis | Offline protocol tests |
| One task snapshot | GET tasks/{escaped task ID} | Offline protocol tests |

## Initialization

```csharp
var options = new AliImageOptions
{
    Id = "ali-image", Model = configuredNativeModel, ApiKey = imageApiKey,
    BaseAddress = configuredRegionalRoot // includes /api/v1/; choose workspace/region explicitly
};
var adapter = new AliImageAdapter(httpClientFactory, options);
var result = await adapter.GenerateNativeAsync(new()
{
    PublicModel = "pictures",
    Parameters = JsonNode.Parse("""{"input":{"messages":[{"role":"user","content":[{"text":"a cat"}]}]},"parameters":{"watermark":false}}""")!.AsObject()
}, cancellationToken);
var imageUrl = result.Images[0].Url; // no download
var tasks = new AliImageTaskClient(httpClientFactory, options, AliImageTaskOperation.ImageGeneration);
var submitted = await tasks.SubmitAsync(nativeRequest, cancellationToken);
var snapshot = await tasks.GetAsync(submitted.Reference, cancellationToken); // exactly one GET
```

Register `AliImageAdapter` in existing `AddImage` generation routes for common prompt-based requests. Their `Parameters` contains native root fields such as nested `parameters`; `input` cannot override the common prompt. Use directly owned `GenerateNativeAsync` / `StreamNativeAsync` for complete original `input.messages`, image references and extension fields. Direct `PublicModel` is metadata, not route selection. Fixed model/root/Bearer identity comes from options. Async operation is explicit, never inferred from model names.

## Protocol and limits

`Data` retains the complete original JSON. `Images` extracts original `output.choices[].message.content[].image` or legacy `output.results[].url`; image item `Data` keeps original field names. Text-only native results are valid. Unknown fields, signed URL spelling, large integers, false/null/zero, usage and task-local failures remain unchanged. An overall nonempty native `code` raises `ImageUpstreamException` even on HTTP 200; task-local FAILED/CANCELED remain task snapshots, including partial outputs. Unknown task states remain nonterminal.

Explicit SSE requires `parameters.enable_interleave=true`; the stream method adds `parameters.stream=true` only if absent, rejects conflicting modes and sends `X-DashScope-SSE: enable`. Async submission sends `X-DashScope-Async: enable` and can retain native interleave parameters without starting a stream. Native events, SSE name/id/retry and cumulative usage pass through; usage is never summed. Intermediate `output.finished:true` does not complete a stream. Every observed choice must reach native `finish_reason:stop`; choice indices and request IDs remain associated. Terminal frames are withheld until EOF, length/byte checks and owned-resource disposal succeed. No synthetic completion, JSON-to-SSE fallback, reconnect or replay.

Task references bind root, actual model, credential identity, implementation, submit operation, limits, client and public label. They are correlation data, not authorization tokens. Query checks identity and returned task ID. Streams/JSON have configurable raw byte limits. The borrowed factory stays caller-owned; per-call clients and messages are disposed. Configure handlers without retries, hedging or redirects. No background polling, media fetching, file upload service, backend or billing. Credentials are supplied by the application, never discovered from other packages.

Live status: **not exercised with real Ali credentials**. Tests use independent native JSON/SSE fixtures and fake HTTP handlers only. Primary references: [Wan image API](https://www.alibabacloud.com/help/en/model-studio/wan-image-generation-api-reference), [text-to-image API](https://www.alibabacloud.com/help/en/model-studio/text-to-image-v2-api-reference). No model catalog or automatic address migration is maintained.
