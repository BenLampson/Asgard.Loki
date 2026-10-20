# Asgard.Loki.Image.XAI

Native xAI JSON image editing in an independent Image provider. References only Image, Infrastructure and HTTP factory support; no dependency on Image.OpenAI, LLM or Video providers.

| Operation | Native contract | Support |
| --- | --- | --- |
| POST /v1/images/edits | JSON image.url or ordered images[].url/file_id references | Complete native JSON |
| URL, data URI, existing file ID inputs | Original references and optional type/extensions | Preserved, no fetching or file management |
| Native data[] results | Original URL/Base64, MIME, revision, usage and extensions | Preserved; Decode() is explicit |
| Multipart / editing SSE | Different transport contract | Explicitly unsupported by this entry |
| Complete image generation | OpenAI-shaped native JSON | Use the separately installed Image.OpenAI verified xAI generation profile |

## Initialization

```csharp
var edit = new XaiImageEditAdapter(httpClientFactory, new()
{
    Id = "xai-edits", Model = configuredNativeModel, ApiKey = imageApiKey,
    Endpoint = new("https://api.x.ai/v1/images/edits") // choose the complete target explicitly
});
await using var client = new LokiClientBuilder().AddImage(new()
{
    EditRoutes = [new() { Model = "pictures", Targets = [edit] }]
}).Build();
var result = await client.Image.EditAsync(new()
{
    Model = "pictures", Prompt = "Render this as a pencil sketch.",
    Parameters = new() { ["image"] = JsonSerializer.SerializeToElement(new
        { url = sourceImageUrl, type = "image_url" }) }
}, cancellationToken);
var outputUrl = result.Images[0].Url; // no download
```

The existing ImageEditRequest carries native root settings in Parameters. Single image uses image:{url:...} or image:{file_id:...}; multi-image uses an ordered images array of those objects, mixing source kinds when supported by the chosen model. Existing file IDs remain opaque model inputs. Exactly one of image/images and one source per reference must be explicit; sources are never selected by priority, renamed into image_url or converted to uploads. No files are opened, fetched, uploaded or managed. Optional native type and extension fields remain unchanged. Configure n, response_format, aspect_ratio and other native settings explicitly; no model catalog, format/size defaults, source reordering or automatic media conversion.

Direct calls use Model only as public metadata; options bind actual upstream model, complete endpoint and independent Bearer key. Parameters are frozen before factory creation/first await, including unknown fields and exact integers. Extra model/prompt overrides and stream=true fail locally. This adapter does not advertise optional editing SSE; multipart fails before reading or disposing borrowed uploads.

## Results, errors and ownership

Data retains the complete native JSON, including nullable fields, MIME, revised_prompt, original usage and extensions. URL spelling remains unchanged and Base64 is decoded only by explicit GeneratedImage.Decode(). Empty native arrays stay empty; no images, created timestamps, token/cost estimates or counts are fabricated. Native API errors and HTTP failures use ImageEditUpstreamException with original status/body/content type, including non-UTF8/binary HTTP error bodies. Known successful image payload fields, duplicate JSON members, UTF-8, raw byte limits and declared lengths are checked before publication.

The application owns the borrowed HTTP factory. Each call owns/disposes its request, response and client before returning a result, observes cancellation and never retries or switches targets. Configure handlers without retries, hedging or redirects. Credentials are excluded from options JSON. No gateway host, tool execution, polling, background workers, file-management service, billing or cross-capability coordinator.

For xAI complete image generation, install [Image.OpenAI](../Asgard.Loki.Image.OpenAI/README.md) separately and configure its verified xAI native JSON profile with a distinct target. There is no package dependency, generated wrapper, automatic brand detection or edit-to-generation fallback. This native edit provider is needed because xAI JSON image.url references differ from the OpenAI reference/multipart edit contract.

Live status: **not verified with real xAI image credentials**. Independent complete JSON vectors and fake HTTP test protocol/ownership only, not model acceptance or image quality. Primary references: [editing](https://docs.x.ai/developers/model-capabilities/images/editing), [multi-image editing](https://docs.x.ai/developers/model-capabilities/images/multi-image-editing), [existing file inputs](https://docs.x.ai/developers/model-capabilities/imagine/files/inputs), [Images API](https://docs.x.ai/developers/rest-api-reference/inference/images). Deferred image operations and other newer endpoints are outside this synchronous edit contract.
