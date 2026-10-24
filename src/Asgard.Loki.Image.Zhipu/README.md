# Asgard.Loki.Image.Zhipu

Native Zhipu v4 complete image generation. Independent Image provider, depending only on Image, Infrastructure and standard HTTP factory support.

| Native contract | Support |
| --- | --- |
| POST /api/paas/v4/images/generations | Original complete JSON |
| Native quality / size / watermark_enabled / user_id and extensions | Original caller values, including explicit false/zero/null |
| Original data[] image carriers | url/image_url and b64_json/b64_image preserved |
| content_filter, created, request_id, extendParam, usage and unknown fields | Complete original envelope |
| SSE, async/newer image operations, v3 authentication | Outside this complete v4 entry |

## Initialization

```csharp
var adapter = new ZhipuImageAdapter(httpClientFactory, new()
{
    Id = "zhipu-images", Model = configuredNativeModel, ApiKey = imageApiKey,
    Endpoint = new("https://open.bigmodel.cn/api/paas/v4/images/generations")
});
var result = await adapter.GenerateAsync(new()
{
    Model = "pictures", Prompt = "a cat",
    Parameters = new() { ["size"] = JsonSerializer.SerializeToElement("1024x1024"),
        ["watermark_enabled"] = JsonSerializer.SerializeToElement(false) }
}, cancellationToken);
var imageUrl = result.Images[0].Url; // original URL, no download
```

Register the adapter in existing AddImage generation routes. The public route Model is metadata; options fix the actual native model, complete endpoint and independent Bearer key. Request root fields are snapshotted before factory creation/first await. Configure actual model-specific parameters explicitly. The SDK does not add default model/size/quality/watermark, rewrite prompts or infer unsupported features from a model name. Model/prompt overrides are rejected. StreamAsync and stream=true fail before I/O; no synthetic stream or automatic method fallback.

## Original results and image views

Data preserves the complete native result, including content filtering, request identifiers, created zero/null/absence, original usage and extension values. No timestamp, image count, token/cost estimate or replacement Base64 is generated. URL results are never fetched. Empty data arrays remain empty with all original filter metadata. Filter-only native envelopes with omitted/null data keep that exact shape and provide an empty image view.

Images supplies a convenience view over each original image item without changing item Data. Url selects the first nonempty original url, then image_url; Base64 selects b64_json, then b64_image. Canonical and alias fields, including explicit nulls, remain in the original JSON even when one is selected for the convenience view. Base64 decoding is explicit through GeneratedImage.Decode(). Known carrier values must be strings or null; unknown fields remain unchanged.

HTTP failures and nonnull root native error use ImageUpstreamException with original status/body/content type, including business failures on HTTP 200. Successful responses require a native data array with valid image carriers, or an explicit native filter-only envelope. Strict UTF-8/duplicate-field, raw byte/declared-length checks and owned resource disposal finish before results are returned. No filtering normalization or metadata arithmetic.

Configure request and total response byte limits and HTTP handlers without retries, hedging or redirects. Each call owns its request/response/client; the factory stays caller-owned. Credentials are application-provided and excluded from options JSON. No generation retry, failover, model catalog, file management, media download, backend or billing. This provider does not reference another capability or provider package.

Live status: **not verified with real Zhipu image credentials**. Tests use independent complete native vectors and fake HTTP; protocol preservation does not establish real model/parameter acceptance or image quality. [Official v4 image generation](https://docs.bigmodel.cn/api-reference/模型-api/图像生成). Old v3 and newer async endpoints are not inferred from this v4 complete-call support.

## Unified generation handler

`ZhipuImageGenerationHandler` can be reused across explicitly configured official or compatible relay targets. Register the handler object or name under `LokiImageProvider`, then create a client through `LokiImageFactory`. Unified optional parameters require actual-target declarations; unsupported fields/values and native collisions fail before generation HTTP. Full native access and existing adapters remain available. Results keep original JSON and exact HTTP bytes; binary retrieval is explicit. See [registration, supported mapping matrix, limits and examples](../../docs/image-generation.md). Verification is offline only; no real model or parameter-effect validation is claimed.
