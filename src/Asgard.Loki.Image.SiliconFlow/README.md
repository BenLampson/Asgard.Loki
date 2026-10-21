# Asgard.Loki.Image.SiliconFlow

Native SiliconFlow images/generations in an independent Image provider. References only Image, Infrastructure and standard HTTP factory support.

| Contract | Support |
| --- | --- |
| POST /v1/images/generations | Complete original native JSON |
| image_size, batch_size, negative_prompt, seed, inference/guidance/cfg, image/image2/image3 and extensions | Original root fields, including explicit zero/false/null |
| Native images / timings / seed envelope | Complete Data and original image item views |
| Native SSE, async tasks, multipart conversion | Unsupported by this entry |

## Initialization

```csharp
var adapter = new SiliconFlowImageAdapter(httpClientFactory, new()
{
    Id = "siliconflow-images", Model = configuredNativeModel, ApiKey = imageApiKey,
    Endpoint = new("https://api.siliconflow.com/v1/images/generations") // explicitly select service/region
});
var result = await adapter.GenerateAsync(new()
{
    Model = "pictures", Prompt = "a cat",
    Parameters = new() { ["image_size"] = JsonSerializer.SerializeToElement("1024x1024"),
        ["batch_size"] = JsonSerializer.SerializeToElement(1), ["seed"] = JsonSerializer.SerializeToElement(0) }
}, cancellationToken);
var imageUrl = result.Images[0].Url; // unchanged, no download
```

Register in existing AddImage generation routes. Direct calls label metadata with public Model; options bind actual native model, endpoint and independent Bearer credential. All request fields are copied before factory creation/first await. Use native parameter names and values explicitly; size/n are not mapped into image_size/batch_size, and model defaults, seed defaults, image dimensions and reference URLs are not guessed or rewritten. Unknown fields and exact integers remain original. Model/prompt overrides are rejected. StreamAsync and stream=true fail locally; no synthetic SSE or method fallback.

## Preserved results and boundaries

Data retains the complete images/timings/seed envelope, including every image object and extension field. Images extracts original native images[].url, preserving original item JSON and URL spelling. No OpenAI data array, created timestamp, usage estimate or metadata arithmetic is added. Empty native image arrays remain empty. The caller handles expiring URLs and any explicit download. Native code/message errors and HTTP errors retain original status/body/content type through ImageUpstreamException, including a native nonzero code on HTTP 200. Invalid JSON, encoding, image envelopes and declared lengths fail validation.

Request and total response bytes have explicit configurable limits. Per-call HTTP message/content/response/client are owned and disposed; the factory remains caller-owned. Configure handlers without retries, hedging or redirects. No generation retry, target failover, implicit image fetching, background polling, generic files, backend or billing. Credentials are supplied by the application and excluded from options JSON.

Live status: **not verified with real SiliconFlow image credentials**. Independent complete offline vectors and fake HTTP verify protocol and ownership only; native parameter/model acceptance and image quality remain unverified. [Official Create image API](https://docs.siliconflow.com/en/api-reference/images/images-generations). Model-specific field availability and service roots are configured by the caller; no latest model catalog or implicit migration is maintained.
