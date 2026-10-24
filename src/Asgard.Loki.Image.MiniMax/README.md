# Asgard.Loki.Image.MiniMax

MiniMax native synchronous image_generation in an independent Image provider. Depends only on Image, Infrastructure and standard HTTP factory support.

| Native operation | Support |
| --- | --- |
| POST /v1/image_generation | Complete original JSON |
| Native image settings and subject_reference inputs | Original root parameters, no rewriting or media fetching |
| data.image_urls / data.image_base64 | Original arrays plus explicit Image convenience views |
| metadata / base_resp | Complete original fields, including partial/empty output counts |
| SSE / async polling / multipart conversion | Unsupported by this native entry |

## Initialization

```csharp
var adapter = new MiniMaxImageAdapter(httpClientFactory, new()
{
    Id = "minimax-images", Model = configuredNativeModel, ApiKey = imageApiKey,
    Endpoint = new("https://api.minimax.io/v1/image_generation") // choose region explicitly
});
var result = await adapter.GenerateAsync(new()
{
    Model = "pictures", Prompt = "a cat",
    Parameters = new() { ["aspect_ratio"] = JsonSerializer.SerializeToElement("16:9"),
        ["response_format"] = JsonSerializer.SerializeToElement("url"),
        ["prompt_optimizer"] = JsonSerializer.SerializeToElement(false) }
}, cancellationToken);
var imageUrl = result.Images[0].Url; // no download
```

Register the adapter in existing AddImage generation routes. Direct calls use their Model only as public metadata; the actual native model is fixed in options. Prompt and all native extra root parameters are copied before factory creation/first await. Configure aspect_ratio, n, response_format (`url` or `base64`), prompt_optimizer, aigc_watermark, subject_reference and native extensions explicitly. No model/format defaults, OpenAI size/aspect mapping, b64_json alias rewriting, URL normalization or reference downloading are performed. Extra model/prompt overrides are rejected. StreamAsync and stream=true fail locally; stream=false stays original.

## Original results and boundaries

Data is the complete original response. Native nonzero base_resp.status_code raises ImageUpstreamException with original status/body/content type even on HTTP 200. Missing or malformed native status/data fail protocol validation. Metadata success/failure counts, unknown fields, exact integers, false/null/zero and empty image arrays remain intact; n does not become a promise of successful images. No created timestamp, usage estimate or success count is fabricated.

Images exposes original URL entries followed by original Base64 entries, retaining the JSON string item in each GeneratedImage.Data. Arrays are independent; the SDK does not zip or assume correspondence between them. URL spelling remains unchanged. Base64 is decoded only through explicit Decode(); no automatic image download or file writing.

Configurable UTF-8 request and total response byte limits apply. Duplicate JSON fields, invalid UTF-8, invalid native envelopes and declared-length mismatch fail. Each call owns/disposes its message, response and HTTP client; the factory remains caller-owned. Disable handler retries, hedging and redirects. Credentials are independent from speech/video, supplied by the application and omitted from options JSON. No automatic retry, failover, gateway host, file management, backend or billing.

Live status: **not verified with real MiniMax image credentials**. Independent offline JSON vectors and fake HTTP test protocol/ownership, not image quality or model acceptance. Primary references: [text-to-image](https://platform.minimax.io/docs/api-reference/image-generation-t2i), [image-to-image](https://platform.minimax.io/docs/api-reference/image-generation-i2i).

## Unified generation handler

`MiniMaxImageGenerationHandler` can be reused across explicitly configured official or compatible relay targets. Register the handler object or name under `LokiImageProvider`, then create a client through `LokiImageFactory`. Unified optional parameters require actual-target declarations; unsupported fields/values and native collisions fail before generation HTTP. Full native access and existing adapters remain available. Results keep original JSON and exact HTTP bytes; binary retrieval is explicit. See [registration, supported mapping matrix, limits and examples](../../docs/image-generation.md). Verification is offline only; no real model or parameter-effect validation is claimed.
