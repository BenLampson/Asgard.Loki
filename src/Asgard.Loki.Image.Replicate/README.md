# Asgard.Loki.Image.Replicate

Native Replicate **model image predictions** and explicit model-image input upload for the pure .NET SDK. Dependencies are Image, Infrastructure and standard HTTP factory support.

## Support matrix

| Operation | SDK entry | Native contract |
| --- | --- | --- |
| Complete image generation | `.Image.GenerateAsync` with `ReplicateImageAdapter` | POST `models/{owner}/{name}/predictions`, Bearer, `Prefer: wait`, original `input` fields; only explicit native success becomes a complete result |
| JSON image editing | `.Image.EditAsync` with the same separately registered target | Original `input.image_prompt` reference and other native input fields; no implicit upload/download |
| Full native prediction | `adapter.PredictAsync(ReplicateNativeImageRequest)` | Complete root JSON including `input`, webhook settings and unknown fields; returns the original prediction at any native status |
| Explicit image-model input upload | `adapter.UploadImageInputAsync(ImageUpload)` | One multipart POST `files`, one `content` part; original upload JSON and `urls.get` retained |
| Original output views | `ReplicateImagePrediction.Images` / completed result `Images` | Scalar string or array string image references, original order and string JSON retained |

See [model predictions](https://replicate.com/docs/reference/http#models.predictions.create), [sync mode](https://replicate.com/docs/topics/predictions/create-a-prediction#sync-mode) and the official [input upload SDK contract](https://github.com/replicate/replicate-javascript/blob/main/index.test.ts). This package does not provide version/deployment prediction endpoints, model catalogs, training, general file management, a task host, polling, result downloading, SSE consumption or gateway format conversions.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.Replicate;
using System.Text.Json;

var target = new ReplicateImageAdapter(httpClientFactory, new()
{
    Id = "replicate-images",
    Model = selectedOwnerAndModelName, // Explicit native owner/name, no default.
    ApiKey = imageApiKey,
    BaseAddress = new Uri("https://api.replicate.com/v1/")
});
// The application owns the factory; disable retries, hedging and redirects.
await using var loki = new LokiClientBuilder().AddImage(new()
{
    Routes = [new() { Model = "pictures", Targets = [target] }],
    EditRoutes = [new() { Model = "picture-edit", Targets = [target] }]
}).Build();
var result = await loki.Image.GenerateAsync(new()
{
    Model = "pictures", Prompt = "A paper boat on a blue lake",
    Parameters = new()
    {
        ["aspect_ratio"] = JsonSerializer.SerializeToElement("3:2"),
        ["seed"] = JsonSerializer.SerializeToElement(0)
    }
}, cancellationToken);
```

For these public convenience requests, `Parameters` contains native **input fields**, placed alongside the original `input.prompt`. No `size`/`quality`/`n`/format mapping, prompt upsampling default or output count is inferred. Public `Model` remains route metadata; the actual model resource and credentials come from the configured complete target. Root fields and native operations that omit prompt use the explicit supplier request below.

## Complete native JSON and prediction outcomes

```csharp
var prediction = await target.PredictAsync(new()
{
    PublicModel = "pictures",
    Parameters = new()
    {
        ["input"] = new System.Text.Json.Nodes.JsonObject
        {
            ["prompt"] = "A paper boat", ["seed"] = 0,
            ["prompt_upsampling"] = false
        },
        ["webhook"] = applicationProvidedCallbackUrl,
        ["webhook_events_filter"] = new System.Text.Json.Nodes.JsonArray("completed")
    }
}, cancellationToken);
// Inspect Status, IsTerminal and IsSucceeded. No further request is automatic.
// AsCompletedResult() is explicit and succeeds only for native succeeded + no error.
```

`PublicModel` labels metadata only; it does not select another module route. The complete native root/input JSON is frozen before factory creation/first await. False/zero/null, exact integers, URL/data-URI references and unknown fields remain unchanged. The fixed model resource cannot be overridden by root model/version fields. Native model-specific feature acceptance remains upstream.

**`Prefer: wait` can return a prediction that is still starting or processing.** `PredictAsync` retains that original state, ID, partial output, errors, logs, metrics, native model/version, timestamps, URLs and unknown fields. Only exact `succeeded`, `failed` and `canceled` are known terminal states; unknown states remain unknown. `IsSucceeded` additionally requires no native error. HTTP request completion is not prediction completion. The public complete-result generation/edit entry and `AsCompletedResult()` reject incomplete/failed outcomes with `ImageUpstreamException` containing the actual HTTP status, complete native bytes and content type. They do not poll, retry or choose another target.

`Data` is the entire unmodified native prediction. `Images` preserves nonempty scalar/array string outputs as original JSON string items, including partial output while processing or failed. Other output shapes, empty strings and non-string array elements remain in `Data` and are not guessed into image views. A succeeded prediction whose output has been removed can have no image views. Signed URLs are unchanged, and neither `urls.get`, `urls.stream` nor output references are fetched. No timestamps, usage estimates or successful images are invented.

## Explicit image input upload and editing

```csharp
// Input stream ownership stays with the application, at its current position.
var input = await target.UploadImageInputAsync(new()
{
    Content = applicationOwnedImageStream,
    FileName = "reference.png", ContentType = "image/png"
}, cancellationToken);
// Retain input.Data if the application needs native id/checksums/expiry/metadata.
var edited = await loki.Image.EditAsync(new()
{
    Model = "picture-edit", Prompt = "Keep the composition, change the light",
    Parameters = new()
    {
        ["image_prompt"] = JsonSerializer.SerializeToElement(input.Url)
    }
}, cancellationToken);
```

These are two explicit operations. Upload does not start prediction, retry, convert to Base64 or clean up/delete the input. It preserves the entire native upload confirmation. It sends one `content` multipart part with the caller's original filename/type and remaining bytes; no length/position query, seek, rewind, local-path access or pixel conversion occurs. Content type defaults to `application/octet-stream` only when the caller omits it, as in the native upload protocol. The borrowed source is never disposed. The only upload surface is model image input; there are no file list/get/delete/download APIs.

The native editing submission is JSON. Common `EditMultipartAsync` is explicitly unsupported before touching the caller's uploads; use the explicit input upload above and supply `image_prompt`. Common multipart text fields are not silently interpreted as native JSON types. Image streaming is also unsupported: the response stream URL is preserved as metadata rather than opened or converted into fake Image SSE.

## Limits, ownership and verification

The application lends the HTTP factory; each operation owns and disposes its request/client/response. Both JSON result and upload confirmation wait for EOF, byte/declared-length validation, disposal and cancellation checks. HTTP/API errors preserve original bytes/status/content type, including binary HTTP errors. `MaxRequestBytes` defaults to 32 MiB of complete UTF-8 prediction JSON, `MaxResponseBytes` to 64 MiB of raw result/error bytes, and `MaxUploadBytes` to 32 MiB of raw input payload excluding multipart framing. Upload failures can consume a prefix of the borrowed stream and do not rewind it. Local JSON, Unicode, filename, credential and target checks fail explicitly.

Tests use independent native prediction/upload fixtures and fake HTTP. They cover whole input/output snapshots, public routes, pending/failed/canceled/unknown/removed outcomes, original scalar/array views, request snapshots, multipart bytes from the caller's current stream position, upload ownership/errors/caps, unsupported conveniences and byte/length/encoding/cancellation/disposal boundaries. **No real Replicate model, parameter acceptance, image quality, input upload or live availability has been verified.** There are no live-test credentials/environment variables for this package.
