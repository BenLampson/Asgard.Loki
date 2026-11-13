# Asgard.Loki.Image.Volcengine

Native Ark `images/generations` JSON/SSE and independently signed Jimeng Visual `CVProcess` JSON within the independent Image capability. The package references only Image, Infrastructure and standard HTTP factory support. No Video/LLM state or credentials, gateway tasks, billing, polling, file service or media downloader are involved.

## Support matrix

| Operation | SDK entry | Native behavior |
| --- | --- | --- |
| Text/image-input generation | `.Image.GenerateAsync` / `.StreamAsync` with `VolcengineImageAdapter` | Fixed actual model and Bearer target; original `image` string/array, group settings and JSON/SSE |
| Full native JSON including omitted prompt for layer decomposition | `adapter.GenerateNativeAsync(VolcengineNativeImageRequest)` / `.StreamNativeAsync` | Direct call on that fixed target; explicit PublicModel is metadata, not a module route selection |
| JSON complete results | Native `data[]` | Original successful images and per-image error items, URL/Base64, layer metadata, tools, usage and unknown fields |
| Jimeng Visual synchronous image generation | `.Image.GenerateAsync` with `JimengImageAdapter` | Explicit `req_key`, root POST `Action=CVProcess&Version=2022-08-31`, independent AK/SK and optional security token; full original JSON |
| Native SSE | `partial_succeeded`, `partial_failed`, `completed` | Per-image identity/errors and final summary retained, with no OpenAI/Azure event projection |

The [native API](https://console.volcengine.com/ark/region:cn-beijing/docs/image-generation-api) uses the same JSON endpoint for text, image references and native layer settings. Input references are caller-provided HTTP(S) URLs or lowercase-format Base64 data URIs; no reference is downloaded, opened as a path or converted to multipart. Model-specific feature acceptance stays with Ark: the SDK has no model catalog, suffix inference or capability-default injection.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.Volcengine;

var target = new VolcengineImageAdapter(httpClientFactory, new()
{
    Id = "ark-images",
    Model = selectedNativeModelOrEndpoint,
    ApiKey = imageApiKey,
    BaseAddress = new Uri("https://ark.cn-beijing.volces.com/api/v3/")
});
// The application owns the factory; disable retries, hedging and redirects.
await using var loki = new LokiClientBuilder().AddImage(new()
{
    Routes = [new() { Model = "pictures", Targets = [target] }]
}).Build();
var result = await loki.Image.GenerateAsync(new()
{
    Model = "pictures", Prompt = "Original prompt",
    Parameters = new()
    {
        ["image"] = System.Text.Json.JsonSerializer.SerializeToElement(referenceUrl),
        ["watermark"] = System.Text.Json.JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
```

The common convenience request retains its required prompt contract. For native layer decomposition that legitimately omits prompt, use the explicitly owned supplier target:

```csharp
var layers = await target.GenerateNativeAsync(new()
{
    PublicModel = "pictures",
    Parameters = new()
    {
        ["image"] = referenceUrl,
        ["layer_decomposition"] = true,
        ["output_format"] = "png"
    }
}, cancellationToken);
```

This direct native API does not select another configured target; PublicModel only labels result metadata. The actual request model comes from target options, and native parameter model overrides fail. An endpoint's returned native model/version remains in `Data` unchanged even if it differs from the requested endpoint-model spelling. Prompts, signed references, group/layer options, tools, false/zero/null, exact large numbers and unknown JSON are retained. No dummy prompt, model/profile default, group count, host file placeholder or task ID is inserted.

## Streaming and original outcomes

Explicit stream methods add true only when missing and reject false conflicts; complete-result methods reject true. Request JSON is frozen before HTTP creation/stream enumeration. A stream instance cannot be replayed after completion, failure or early stop. Native [stream events](https://console.volcengine.com/ark/region:cn-beijing/docs/image-generation-streaming-responses) keep original JSON and SSE name/id/retry. Partial successes/failures use their original image_index; duplicate/invalid indices fail rather than reassigning another image's result. The SDK does not flatten a per-image `partial_failed` into an overall error or discard other outputs. A summary has no required image payload, and its usage/tools are not recalculated.

Single-image mode (native sequential generation omitted/disabled) may end after its original per-image result without a summary, as described in the [provider streaming guide](https://github.com/volcengine/ark-cli/blob/main/skills/arkcli-gen/references/image-stream.md). The SDK waits for normal EOF/length/resource completion and emits that same event, without inventing completed JSON. Group mode retains incremental per-image outcomes and waits for its native summary, or the documented terminating InternalServiceError outcome. `max_images` is not treated as an exact result promise. Summary/held single-image output is published only after raw length/byte checks and stream/response/client disposal succeed. Whole-request native errors preserve actual status, complete bytes/content type and optional original error event in ImageUpstreamException. JSON success is never converted into fake SSE; an unexpected complete JSON result remains available through the failure's inner upstream exception.

Complete `Data` keeps mixed data[].error items, layer geometry/name/order, native model/times/tools/usage and unknown fields. `Images` is an original-order view of data[] and can include native error items; inspect each item's Data. Neither a normal HTTP result nor stream EOF certifies every image succeeded. There is no charge calculation, automatic Base64 decoding, URL retrieval or tool execution.

`MaxRequestBytes` defaults to 32 MiB; `MaxResponseBytes` defaults to 64 MiB for a complete raw response/error or SSE exchange. Invalid UTF-8/ambiguous JSON, protocol association, length/read/disposal errors and cancellation fail explicitly. The application owns targets and factory; each call owns its own HTTP resources. No automatic retry, reconnect or target switch occurs.

## Jimeng Visual synchronous generation

`JimengImageAdapter` uses the same existing Image generation route with a separate complete native target:

```csharp
var jimeng = new JimengImageAdapter(httpClientFactory, new()
{
    Id = "jimeng-images",
    RequestKey = selectedNativeRequestKey,
    AccessKeyId = imageAccessKey,
    SecretAccessKey = imageSecretKey,
    // SecurityToken = suppliedTemporaryCredentialToken,
    BaseAddress = new Uri("https://visual.volcengineapi.com/")
});
// Register jimeng as a target of an Image route, then call Image.GenerateAsync.
var result = await jimeng.GenerateAsync(new()
{
    Model = "pictures", Prompt = "A paper boat on a blue lake",
    Parameters = new()
    {
        ["return_url"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
        ["use_pre_llm"] = System.Text.Json.JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
```

The [official Visual SDK](https://github.com/volcengine/volc-sdk-python/blob/master/volcengine/visual/VisualService.py) defines this root POST, version and `cn-north-1` / `cv` signing scope. The optional constructor `TimeProvider` is borrowed for per-call UTC signatures. Request bytes are frozen before factory creation/signing. AK/SK/token are independent options and excluded from default configuration JSON. The caller manages temporary-credential expiry; the SDK does not discover or refresh credentials. Use a dedicated named client without retries, hedging or redirects.

**Availability checked 2026-10-20:** the official [Jimeng image service page](https://docs.volcengine.com/docs/JimengAI/JimengAI-imagegenerationbillinginstructions?lang=zh) labels image 2.1 as being phased out. Protocol support does not establish availability for a selected `req_key` or account. Supply the actual permitted service identifier explicitly; no model default/catalog, automatic fallback or migration to a different synchronous/asynchronous API is supplied. This adapter covers `CVProcess`, with no SSE, asynchronous task service or multipart conversion.

`req_key` comes only from the target, while public `Model` labels route metadata. The original prompt and all extra native JSON, including image reference arrays, seed/size, zero/false/null, watermark and unknown settings, remain unchanged. No return format or defaults are inserted. HTTP errors and native non-10000 codes/signature errors retain complete bytes/status/content type in `ImageUpstreamException`. On success, `Data` keeps the entire native envelope, including `status`, `time_elapsed`, root/nested request IDs and `rephraser_result`. `Images` exposes Base64 array items followed by URL array items, preserving the order of each array; the arrays are independent and are never zipped or deduplicated. Each view's `Data` is its original JSON string. No local timestamp, image payload, decoding, media download or usage arithmetic is added. Empty native arrays remain empty. Complete results wait for EOF, length/byte limits, disposal and cancellation checks.

## Verification

75 xUnit v3 fake-HTTP cases (39 Ark and 36 Jimeng) using independent complete JSON/SSE vectors cover scalar/array references, native omitted prompt, full mixed/layer results, group partial failures, single-image EOF, identity/order, snapshots, overall errors, byte/length/cancellation/disposal boundaries and no replay. Independent Jimeng exact-byte signing vectors include permanent and temporary credentials, original arrays/metadata, native errors and resource boundaries. No real Ark/Jimeng model/endpoint, image quality, feature acceptance, pixel validity or live availability has been verified. This package has no live-test environment variables.

## Unified generation handler

`VolcengineImageGenerationHandler` can be reused across explicitly configured official or compatible relay targets. Register the handler object or name under `LokiImageProvider`, then create a client through `LokiImageFactory`. Unified optional parameters require actual-target declarations; unsupported fields/values and native collisions fail before generation HTTP. Full native access and existing adapters remain available. Results keep original JSON and exact HTTP bytes; binary retrieval is explicit. See [registration, supported mapping matrix, limits and examples](../../docs/image-generation.md). Verification is offline only; no real model or parameter-effect validation is claimed.
