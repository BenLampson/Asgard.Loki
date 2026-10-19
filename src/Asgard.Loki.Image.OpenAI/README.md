# Asgard.Loki.Image.OpenAI

OpenAI native image generation and editing provider for Asgard.Loki.

| Feature | Support |
| --- | --- |
| `POST /v1/images/generations` | Yes |
| Complete JSON results | Yes |
| SSE partial/final image events | Yes, when supported by the upstream model |
| Base64 and URL outputs | Preserved; Base64 decoding is available on demand |
| Quality, size, transparency, output format, image count and future parameters | Passed through |
| `POST /v1/images/edits` JSON | Yes, explicit native `images` references and optional `mask` object |
| `POST /v1/images/edits` multipart | Yes, ordered `image[]` uploads and optional separate `mask` upload |
| Editing SSE | Pending; nonstream editing rejects explicit `stream=true` |
| Image variations | Not implemented |

Create `OpenAiImageAdapter(IHttpClientFactory, OpenAiImageOptions)` and register it in `ImageOptions.Routes`. Configure the actual upstream model and API Key in the adapter options; use the route alias in requests. Optional organization/project headers and a custom base address are supported.

The default named HTTP client is `Asgard.Loki.Image.OpenAI`. Disable generation POST retries and hedging. Prompts and unknown JSON fields are not rewritten. Incomplete streams fail instead of claiming completion. URLs are returned without automatic downloads.

Local tests verify request/response and SSE behavior. No live image generation or provider entitlement check is performed by those tests.

## Native image editing

Create `OpenAiImageEditAdapter(IHttpClientFactory, OpenAiImageOptions)` and register it separately in `ImageOptions.EditRoutes`. The configuration type is shared with generation; the adapter/target registration is explicit and no generation target is borrowed. Public route aliases stay separate from actual upstream models. Both edit methods send one POST to `images/edits` under the configured versioned base address and preserve configured Bearer/organization/project headers.

```csharp
var edit = new OpenAiImageEditAdapter(httpFactory, new OpenAiImageOptions
{
    Id = "image-edit", Model = actualImageModel, ApiKey = apiKey
});
await using var loki = new LokiClientBuilder().AddImage(new ImageOptions
{
    EditRoutes = [new() { Model = "pictures", Targets = [edit] }]
}).Build();
var result = await loki.Image.EditAsync(new ImageEditRequest
{
    Model = "pictures", Prompt = "Keep the subjects and change the background.",
    Parameters = new()
    {
        ["images"] = JsonSerializer.SerializeToElement(new[] { new { image_url = sourceUrl } }),
        ["mask"] = JsonSerializer.SerializeToElement(new { file_id = uploadedMaskId }),
        ["output_format"] = JsonSerializer.SerializeToElement("webp")
    }
}, cancellationToken);
```

Import `Asgard.Loki`, `Asgard.Loki.Image`, `Asgard.Loki.Image.OpenAI` and `System.Text.Json`. JSON references carry exactly one native `image_url` or `file_id`; attached unknown fields retain their types and values. File IDs/URLs/data URLs are passed unchanged, without downloading, uploading/resolving files, inventing MIME types or converting to multipart. Native model/account reference eligibility remains upstream-owned.

For files, explicitly call `loki.Image.EditMultipartAsync(new ImageMultipartEditRequest { ... }, cancellationToken)` with caller-owned `ImageUpload` entries and optional separate `Mask`. Images use native repeated `image[]` parts in their original order; mask uses `mask`. Literal `ImageMultipartField` entries retain ordered/repeated unknown names, bracket syntax, empty strings and raw text. Known scalar duplicates, model/prompt/image/mask overrides, invalid UTF-8/header controls, reused stream objects and unreadable inputs fail before HTTP. Optional content types are validated as MIME values, never guessed from file names.

The caller keeps streams alive through completion. Uploads start at their current positions, do not query length/position, rewind, close, decode, resize or validate mask pixels/dimensions. The official masked-edit contract associates a mask with the first image; this SDK preserves that native ordering and separate mask role. It never remaps masks or checks their visual effect. Arbitrary fixture bytes in tests are not accepted image-format evidence.

Neither method injects `stream`, quality, size, format or other native defaults. Explicit false remains false; true must use a future dedicated edit streaming implementation and is rejected here. There is no automatic JSON/multipart fallback, generation fallback, retry or failover. Disable POST retries/hedging and unintended replay in the application-owned HTTP pipeline; multipart parts also reject a second serialization of borrowed streams.

Complete responses retain native JSON/GetRawText, ordered Base64/URL items, revised prompts, exact numbers, usage and unknown fields. Known response container/image fields and duplicate JSON members are validated without decoding images or recalculating usage. A successful result is returned only after normal body EOF, declared-length verification and response/client disposal. Invalid UTF-8, unexpected SSE/media, tail errors, cancellation or disposal failure do not return success. `ImageEditUpstreamException` retains actual HTTP status, original error bytes and content type, including non-UTF-8 errors and native business errors in 2xx responses. Existing generation exception behavior is unchanged.

The [official image guide](https://developers.openai.com/api/docs/guides/image-generation) documents file/mask editing; the official endpoint OpenAPI exposes JSON reference requests and multipart curl examples. This checkpoint has 119 new offline edit cases plus the existing five generation cases (124 total), including four independent complete request goldens (JSON and three MIME variants), full native response JSON and failure/ownership/encoding boundaries. Tests use substitute HTTP handlers only. Native editing SSE, variations, actual OpenAI image/account acceptance and image/mask quality remain unverified or pending; no real OpenAI requests are made.
