# Asgard.Loki.Image.Azure

Native deployment-scoped Azure image generation and multipart editing. This package references only Image, Infrastructure and standard HTTP factory support. It uses an explicit resource root, deployment, API version and API Key; it does not infer a model, borrow LLM/audio credentials or provide Azure resource management.

## Support matrix

| Operation | Native wire | Support |
| --- | --- | --- |
| `AzureImageAdapter.GenerateAsync` | POST `openai/deployments/{deployment}/images/generations?api-version=...`, JSON | Complete original result, including all images, filtering, usage and unknown fields |
| `AzureImageEditAdapter.EditMultipartAsync` | POST `openai/deployments/{deployment}/images/edits?api-version=...`, multipart | One `image` or multiple `image[]` uploads, separate optional mask and ordered literal text fields |
| JSON image-reference editing | Different wire from the documented dated multipart operation | Unsupported; fails locally without downloading references or converting inputs |
| Native generation/editing SSE | Explicit streaming operations | Not implemented; `stream=true`/streaming entry fails locally, never creates events from JSON |

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

## Ownership and results

Uploads are single-use borrowed streams from the caller's current position. The SDK never opens a filename, queries stream length/position, rewinds, buffers the upload to compute a length, decodes pixels, infers MIME from an extension or disposes the original input. Images and mask need distinct readable streams. Multipart content refuses implicit serialization replay; failures do not retry or change targets. Keep inputs alive until the call ends. Factory and targets remain application-owned; each call disposes its own request, response and client before publishing success. Factory default Authorization/api-key headers are cleared so only the fixed Azure credential is used.

Complete results retain the original JSON envelope and image order, Base64 strings, signed URL spelling, revised prompts, Azure prompt/content filter objects, exact usage counts and unknown fields. The SDK does not aggregate usage, compute charges, automatically decode/download images or interpret filter flags as a separate policy verdict. HTTP/native business failures retain actual status, original error bytes and content type in `ImageUpstreamException` or `ImageEditUpstreamException`, including 2xx native error objects. Partial/error-only items cannot become complete image success. Invalid UTF-8/JSON, duplicate fields, missing payloads, length/read/disposal errors or cancellation fail explicitly.

`MaxRequestBytes` defaults to 4 MiB for JSON or multipart text/metadata; multipart framing and file bytes are not prebuffered to enforce a total wire length. `MaxUploadBytes` limits each remaining upload to 50 MiB by default. `MaxResponseBytes` bounds complete response/error bytes at 64 MiB. These are local limits, not validation of Azure model-specific pixel, format, count or mask restrictions.

## Verification

xUnit v3 tests compare independent JSON requests, two complete multipart byte goldens and full original Azure result/error envelopes using fake HTTP only. Tests cover fixed URI/authentication, input/result snapshots, exact values, image/mask ownership, nonseekable uploads, replay refusal, byte bounds, read/disposal failures and cancellation. No real Azure credentials, image/model acceptance, filtering outcome, pixel quality or live availability have been verified. This package has no live-test environment configuration.
