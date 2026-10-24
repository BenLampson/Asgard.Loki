# Asgard.Loki.Image.Azure

Native deployment-scoped Azure image generation and multipart editing. This package references only Image, Infrastructure and standard HTTP factory support. It uses an explicit resource root, deployment, API version and API Key; it does not infer a model, borrow LLM/audio credentials or provide Azure resource management.

## Support matrix

| Operation | Native wire | Support |
| --- | --- | --- |
| `AzureImageAdapter.GenerateAsync` | POST `openai/deployments/{deployment}/images/generations?api-version=...`, JSON | Complete original result, including all images, filtering, usage and unknown fields |
| `AzureImageEditAdapter.EditMultipartAsync` | POST `openai/deployments/{deployment}/images/edits?api-version=...`, multipart | One `image` or multiple `image[]` uploads, separate optional mask and ordered literal text fields |
| JSON image-reference editing | Different wire from the documented dated multipart operation | Unsupported; fails locally without downloading references or converting inputs |
| `AzureImageAdapter.StreamAsync` | Generation JSON with explicit `stream=true`; native SSE | Original partial/final image events, unknown JSON and SSE name/id/retry |
| `AzureImageEditAdapter.StreamMultipartEditAsync` | Native streaming multipart edits | Borrowed images/mask, original edit events and whole-call completion |

The [dated Microsoft reference](https://learn.microsoft.com/en-us/azure/foundry/openai/reference-preview) documents JSON generation and multipart edits. Deployment names retain literal dots; versions are one escaped query value. No version/model defaults or v1 endpoint profile are added. A model deployment's access, feature acceptance and lifecycle remain determined by Azure.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.Azure;

var options = new AzureImageOptions
{
    Id = "azure-image-target",
    Endpoint = azureResourceRoot,
    Deployment = deployedImageName,
    ApiVersion = selectedApiVersion,
    ApiKey = imageApiKey
};
// Application-owned factory: disable retries, hedging and redirects.
await using var loki = new LokiClientBuilder().AddImage(new()
{
    Routes = [new() { Model = "pictures", Targets = [new AzureImageAdapter(httpClientFactory, options)] }],
    EditRoutes = [new() { Model = "pictures", Targets = [new AzureImageEditAdapter(httpClientFactory, options)] }]
}).Build();
var generated = await loki.Image.GenerateAsync(new()
{
    Model = "pictures", Prompt = "Original prompt"
}, cancellationToken);

// Opening files and keeping streams alive belong to the application.
await using var input = File.OpenRead(imagePath);
var edited = await loki.Image.EditMultipartAsync(new()
{
    Model = "pictures", Prompt = "Original editing instruction",
    Images = [new() { Content = input, FileName = "input.png", ContentType = "image/png" }],
    Fields = [new("n", "1"), new("quality", "high")]
}, cancellationToken);
```

Generation/editing targets register independently and need no shared Session. The public route selects the configured adapter; the deployment is the actual HTTP target and `ImageResultMetadata.UpstreamModel`. Optional `BodyModel` sends an explicitly configured body/form `model`; null omits it. Public route names are never sent as native models. No generation parameter defaults are injected; explicit false/zero, original prompts, extension JSON and ordered repeated unknown form fields remain original. Root model/prompt/input overrides, duplicate known form fields, invalid Unicode/headers and ambiguous JSON fail before HTTP.

## Native streaming

Use `.Image.StreamAsync` for generation and `.Image.StreamMultipartEditAsync` for native multipart edits. Each explicit stream call snapshots request fields before enumeration, adds `stream=true` only when omitted and rejects a conflicting false. Complete-result methods reject true instead of silently switching protocols. Generation and editing retain their own native event families. JSON-reference editing, including `StreamEditAsync`, remains unsupported for the dated multipart target.

```csharp
await foreach (var frame in loki.Image.StreamAsync(new()
{
    Model = "pictures", Prompt = "Original prompt",
    Parameters = new()
    {
        ["n"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
        ["partial_images"] = System.Text.Json.JsonSerializer.SerializeToElement(3)
    }
}, cancellationToken))
{
    // frame.Data is the complete original native event; no automatic pixel decoding.
}
```

Streams preserve original JSON, Base64 strings, exact counts/timestamps, unknown events and independent SSE name/id/retry. Partial indices remain native indices; the SDK does not use them to invent cross-image IDs or assemble previews. Requested partial counts do not promise an exact number of progress events. For native output count `n` (documented 1–10, omitted validation default 1), the stream must supply that many native completed images. The SDK does not inject n or turn a single-image response into a multi-image result. Model/version acceptance is still determined by Azure.

Progress before the first completed image streams immediately. Starting with the first completion, the original event tail is held in order until normal EOF, byte/length checks and source/response/client disposal all succeed. This avoids publishing a completed image before a late error is seen. For edits, `ImageEditEvent.IsCompleted` marks completion of the whole call, on the last native completed image; earlier completed images keep their native JSON and have false. Optional `[DONE]` is accepted only after all native completed images and never becomes a synthetic JSON event or substitutes for missing images. Duplicate/excess/missing completions, wrong operation/event identities, malformed tails, native errors, cancellation and disposal failures cannot first publish successful completion. A non-SSE JSON response is not projected into events; its original body remains available through the failure (the inner upstream exception for an otherwise successful JSON result).

Stream enumeration is single-use, including after early stop, and does not replay a generation/upload. Inputs remain borrowed and must stay readable until enumeration ends. Open a fresh input or explicitly reposition it yourself for a new call. Early disposal closes the HTTP exchange without closing borrowed images/mask. There is no automatic reconnect, retry, target switch, background job or common LLM/Audio workflow.

## Ownership and results

Uploads are single-use borrowed streams from the caller's current position. The SDK never opens a filename, queries stream length/position, rewinds, buffers the upload to compute a length, decodes pixels, infers MIME from an extension or disposes the original input. Images and mask need distinct readable streams. Multipart content refuses implicit serialization replay; failures do not retry or change targets. Keep inputs alive until the call ends. Factory and targets remain application-owned; each call disposes its own request, response and client before publishing success. Factory default Authorization/api-key headers are cleared so only the fixed Azure credential is used.

Complete results retain the original JSON envelope and image order, Base64 strings, signed URL spelling, revised prompts, Azure prompt/content filter objects, exact usage counts and unknown fields. The SDK does not aggregate usage, compute charges, automatically decode/download images or interpret filter flags as a separate policy verdict. HTTP/native business failures retain actual status, original error bytes and content type in `ImageUpstreamException` or `ImageEditUpstreamException`, including 2xx native error objects. Partial/error-only items cannot become complete image success. Invalid UTF-8/JSON, duplicate fields, missing payloads, length/read/disposal errors or cancellation fail explicitly.

`MaxRequestBytes` defaults to 4 MiB for JSON or multipart text/metadata; multipart framing and file bytes are not prebuffered to enforce a total wire length. `MaxUploadBytes` limits each remaining upload to 50 MiB by default. `MaxResponseBytes` bounds complete response/error bytes and each complete raw SSE exchange (including framing/heartbeats) at 64 MiB. These are local limits, not validation of Azure model-specific pixel, format, count or mask restrictions.

## Verification

Tests compare independent JSON requests, four complete multipart byte goldens, original SSE wires and full original Azure result/error envelopes using fake HTTP only. The tests also cover multi-image whole-call completion, native errors and unknown events, exact numeric spelling, request snapshots, single-use enumeration, early stop and final resource/cancellation boundaries. Tests cover fixed URI/authentication, input/result snapshots, exact values, image/mask ownership, nonseekable uploads, replay refusal, byte bounds, read/disposal failures and cancellation. No real Azure credentials, image/model acceptance, filtering outcome, pixel quality or live availability have been verified. This package has no live-test environment configuration.

## Unified generation handler

`AzureImageGenerationHandler` can be reused across explicitly configured official or compatible relay targets. Register the handler object or name under `LokiImageProvider`, then create a client through `LokiImageFactory`. Unified optional parameters require actual-target declarations; unsupported fields/values and native collisions fail before generation HTTP. Full native access and existing adapters remain available. Results keep original JSON and exact HTTP bytes; binary retrieval is explicit. See [registration, supported mapping matrix, limits and examples](../../docs/image-generation.md). Verification is offline only; no real model or parameter-effect validation is claimed.
