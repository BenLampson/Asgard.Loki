# Unified image generation

Unified generation adds a model registry, a factory and reusable protocol handlers. Existing `ImageGenerationRequest`, `.Image.GenerateAsync`, streaming, editing and supplier-native clients remain available. No existing call opts into conversion or downloading.

## Registration and calls

Install `Asgard.Loki` and the image supplier packages you need, at matching versions. The following example uses `Asgard.Loki.Image.OpenAI` and `Asgard.Loki.Image.Volcengine`.

The application supplies `IHttpClientFactory`. Configure the upstream clients without retries, hedging or automatic redirects. Configure a separate image-content client without default credentials, cookies, automatic redirects or retries. The SDK clears its default request headers and never copies generation keys into download requests; custom transport handlers must also preserve that isolation.

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Image.Volcengine;
using System.Text.Json;
using System.Text.Json.Nodes;

// httpFactory and configuration belong to the application.
var openAiHandler = new OpenAiImageGenerationHandler();
var arkHandler = new VolcengineImageGenerationHandler();

// These are declarations for these explicitly selected real models.
// Set the values according to the actual service/model contract.
var singleImage = new Dictionary<string, LokiImageParameterRule>
{
    [nameof(LokiImageGenerationRequest.Count)] = new()
    {
        AllowedValues = [JsonSerializer.SerializeToElement(1)]
    }
};

var registration = new LokiImageProvider
{
    Handlers = new Dictionary<string, ILokiImageGenerationHandler>
    {
        ["openai-images"] = openAiHandler
    },
    Models =
    [
        new LokiImageModel
        {
            Name = "pictures",
            // Default: independent round-robin per client and public model.
            // A custom application-owned ILokiImageProviderSelector is optional.
            Providers =
            [
                new LokiImageUpstreamProvider
                {
                    Id = "official",
                    BaseAddress = new Uri(configuration["Images:Official:Root"]!),
                    UpstreamModel = configuration["Images:Official:Model"]!,
                    ApiKey = configuration["Images:Official:Key"]!,
                    HandlerName = "openai-images",
                    HttpClientName = "image-upstreams",
                    Parameters = singleImage
                },
                new LokiImageUpstreamProvider
                {
                    Id = "relay",
                    BaseAddress = new Uri(configuration["Images:Relay:Root"]!),
                    UpstreamModel = configuration["Images:Relay:Model"]!,
                    ApiKey = configuration["Images:Relay:Key"]!,
                    Handler = openAiHandler, // The same reusable instance.
                    HttpClientName = "image-upstreams",
                    Parameters = singleImage
                },
                new LokiImageUpstreamProvider
                {
                    Id = "ark",
                    BaseAddress = new Uri(configuration["Images:Ark:Root"]!),
                    UpstreamModel = configuration["Images:Ark:Model"]!,
                    ApiKey = configuration["Images:Ark:Key"]!,
                    Handler = arkHandler,
                    HttpClientName = "image-upstreams",
                    Parameters = singleImage
                }
            ]
        }
    ]
};

var factory = new LokiImageFactory(registration, httpFactory,
    new LokiImageContentOptions
    {
        HttpClientName = "image-content",
        MaximumBytes = 32 * 1024 * 1024
    });

// Standalone use:
var images = factory.CreateClient();

// Optional Loki module integration:
await using var loki = new LokiClientBuilder()
    .AddImage(new ImageOptions { GenerationFactory = factory })
    .Build();
var moduleImages = loki.Image.Generation;

var result = await images.GenerateAsync(new LokiImageGenerationRequest
{
    Model = "pictures",
    Prompt = "A watercolor cat beside a sunny window.",
    Count = 1
}, cancellationToken);

// Original JSON plus exact HTTP bytes/status/media type where supplied.
JsonElement originalJson = result.Data;
ImageRawResponse? originalHttp = result.RawResponse;
Console.WriteLine(result.ProviderId);
Console.WriteLine(result.UpstreamModel);
Console.WriteLine(result.State);

if (result.State is LokiImageGenerationState.Succeeded
    or LokiImageGenerationState.PartiallySucceeded)
{
    foreach (var image in result.Images)
    {
        if (image.Error is not null) continue;

        // Explicit: decode Base64, consume supplied bytes, or GET a returned URL.
        var content = await images.GetImageContentAsync(image, cancellationToken);
        ReadOnlyMemory<byte> bytes = content.Bytes;
        string? mediaType = content.ContentType;

        // The application chooses the path, extension and storage lifetime.
        // await File.WriteAllBytesAsync(outputPath, bytes.ToArray(), cancellationToken);
    }
}
```

`LokiImageFactory` freezes registration lists, rules and JSON settings. Each created client has its own default selection state. A handler is a reusable protocol implementation; address, model, key and per-call state belong to the selected invocation. A selector returns one complete target index. An out-of-range selection fails. Generation is attempted once: no parameter-based target filtering, retry, failover or automatic model change.

The optional module property is `.Image.Generation`, so existing calls such as `.Image.GenerateAsync(new() { ... })` keep their original meaning and do not acquire target-typed overload ambiguity.

## Unified parameter declarations

Only explicitly set optional values are mapped. An omitted field remains omitted. Each set field requires both a reliable handler mapping and a `Parameters` declaration on the actual target. Missing declarations fail locally, rather than claiming that a provider or every model under it supports that field.

Use `nameof(LokiImageGenerationRequest.Count)`, etc. as rule keys. `AllowedValues` compares the unified JSON value; `Minimum` and `Maximum` constrain numbers inclusively. A rule with no restrictions explicitly declares support for the field without adding local value limits. Model-specific or parameter-combination validation beyond these declared constraints remains the supplier's native validation; registration is not live capability discovery.

`Size` is an exact positive pixel width/height, represented as `"1024x1024"` in allowed-value declarations. `AspectRatio` is a separate native ratio string. `OutputFormat` describes image encoding, not the URL/Base64 delivery carrier. No quality-to-size mapping, resizing, recompression or count synthesis occurs.

`LokiImageParameterException : ArgumentException` exposes `ProviderId`, `UpstreamModel`, `ParamName`, `RequestedValue`, `Reason` and `SupportedValues`. Capability, range and mapping-conflict failures occur before any generation HTTP request. Globally malformed input and reserved root-field overrides are ordinary argument errors.

| Handler | Reliable optional mappings |
| --- | --- |
| `OpenAiImageGenerationHandler` | Count → n; Size → size; Quality → quality; OutputFormat → output_format; Background → background |
| `VolcengineImageGenerationHandler` | Count=1 → sequential_image_generation=disabled; Size → size; OutputFormat → output_format; Seed → seed; GuidanceScale → guidance_scale; Watermark → watermark |
| `SiliconFlowImageGenerationHandler` | Count → batch_size; Size → image_size; Seed → seed; NegativePrompt → negative_prompt; Steps → num_inference_steps; GuidanceScale → guidance_scale |
| `MiniMaxImageGenerationHandler` | Count → n; AspectRatio → aspect_ratio; Watermark → aigc_watermark |
| `ZhipuImageGenerationHandler` | Size → size; Quality → quality; Watermark → watermark_enabled |
| `GoogleImageGenerationHandler` | AspectRatio → generationConfig.imageConfig.aspectRatio |
| `GoogleVertexImageGenerationHandler` | AspectRatio → generationConfig.imageConfig.aspectRatio; explicit API Key profile |
| `AzureImageGenerationHandler` | Count → n; Size → size; Quality → quality; OutputFormat → output_format; Background → background |
| `AliImageGenerationHandler` / `AliImageTaskGenerationHandler` | Count → parameters.n; Size → parameters.size; Seed → parameters.seed; NegativePrompt → parameters.negative_prompt; Watermark → parameters.watermark |

Handler mapping availability does not declare model support. In particular, Ark's native `max_images` is a maximum, so unified exact `Count > 1` is rejected even if a caller declares it. Use native group-generation parameters for that operation.

Endpoint interpretation stays supplier-specific: OpenAI/Ark/Gemini/Ali use their existing version roots; SiliconFlow/MiniMax/Zhipu use complete native endpoints. Azure's `UpstreamModel` is the deployment, with required `Settings["api_version"]` and optional `Settings["body_model"]`. OpenAI supports optional `Settings["organization"]` / `["project"]`. Vertex takes a complete model resource in `UpstreamModel` and optional `Settings["quota_project"]`; borrowed-token Vertex remains available through its existing native adapter.

Replicate, Midjourney, Jimeng signing and other distinct operations retain their existing native clients/adapters. They do not acquire a generic handler by brand or model-name inference. Implement `ILokiImageGenerationHandler`, or derive from `LokiImageGenerationHandler` to reuse field mapping and an existing adapter. The provider package owns protocol-specific mapping. A configured custom handler must expose a stable `BindingIdentity` for settings that affect native task interpretation. Handler instances, selectors and HTTP factories are borrowed and never disposed by this layer.

## Native parameters and complete native calls

A unified request may include `NativeParameters`. Unknown fields, exact numbers, explicit false/null and native input references remain unchanged. Nested nonoverlapping fields merge; a native leaf, incompatible parent value or case alias colliding with a set unified field fails. No precedence rule silently overwrites a value.

For complete native requests, specify `ProviderId`; a heterogeneous public model must not randomly dispatch native JSON to another protocol.

```csharp
var native = await images.GenerateNativeAsync(new LokiNativeImageGenerationRequest
{
    Model = "pictures",
    ProviderId = "relay",
    Parameters = new JsonObject
    {
        ["prompt"] = "A cat.",
        ["n"] = 2,
        ["vendor_extension"] = new JsonObject { ["enabled"] = false }
    }
}, cancellationToken);
```

Native calls bypass unified parameter declarations but preserve supplier-native validation and fixed-target model binding. Root `model` and method-controlled `stream` cannot override the configured target/method. Ark supports its original omitted-prompt native layer operation; Gemini and Ali accept their complete native input structures. Existing supplier-native clients and existing Image APIs remain directly usable.

## Results, tasks and content

`LokiImageGenerationResult` keeps `NativeResult`, complete `Data`, optional exact `RawResponse`, selected identities, `State` and ordered `Images`. Native image-level errors remain in place; mixed valid images/errors produce `PartiallySucceeded`. Empty results produce `NoImage`, not invented success. Failed/unknown task statuses never become successful because a URL exists.

To explicitly use Ali's modern async image-generation service, register `AliImageTaskGenerationHandler` instead of the synchronous handler. Its unified request mapping is the same, but submission returns a native task reference and an explicit pending/running/unknown/terminal state:

```csharp
// Register this handler on an Ali target:
// Handler = new AliImageTaskGenerationHandler()

var submitted = await images.GenerateAsync(request, cancellationToken);
if (submitted.TaskReference is { } reference)
{
    var serialized = JsonSerializer.Serialize(reference);
    // Persist serialized; the application decides when to query.
    var restored = JsonSerializer.Deserialize<LokiImageTaskReference>(serialized)!;
    var current = await images.GetAsync(restored, cancellationToken); // Exactly one query.
}
```

References bind public model, server, actual model, key identity, HTTP client policy, handler implementation/identity and native-operation settings. Reordering targets or reconstructing a client does not change an existing task's target. Removal/configuration drift, native-reference mismatch and a different returned task ID fail explicitly. References contain no plaintext keys, are correlation data rather than authorization tokens, and do not add cache, background polling or callback hosting. Unknown statuses remain unknown. Existing Ali legacy operation clients retain their original contracts.

Content is opt-in and independent of generation success. Supplied binary takes precedence, followed by Base64 and then URL. Base64 data URIs are accepted; decoding/downloads obey the configured per-image byte limit. Supplier-declared media types are retained for Base64; otherwise known file signatures provide the type, and unknown signatures leave it null rather than guessing. Downloads preserve a real image media type or recognize a known signature, reject non-image/empty content and check complete length/EOF/disposal/cancellation before delivery. No generation credentials are attached. A failed download leaves the original result and URL available; retrying content retrieval never regenerates the image.

## Verification

Fake-HTTP xUnit v3 tests cover independent native wire expectations for all bundled handlers, singleton reuse, official/relay identities, configuration/request snapshots, independent selection state, unsupported fields/ranges/conflicts, native bypass, exact raw responses, legacy source compatibility, content bounds/credential isolation and persistent async task identity. These are offline protocol tests, not real model, parameter-effect or pixel-quality verification.
