# Asgard.Loki.Image.Midjourney

Native image calls to an **explicitly configured unofficial Midjourney proxy**, using the independent Image capability, Infrastructure and standard HTTP factory support. This package is a client library. It supplies no proxy address, Discord connection, accounts, task host, storage or billing service.

## Support matrix

| Native operation | SDK entry | Original result |
| --- | --- | --- |
| `submit/imagine`, `describe`, `blend` | `SubmitAsync` with `Imagine`, `Describe`, `Blend` | Original acknowledgement and bound task reference |
| `submit/change`, `simple-change` | `SubmitAsync` with `Change`, `SimpleChange` | Classic proxy operation; original action/index or content, with no button conversion |
| `submit/action`, `modal` | `SubmitAsync` with `Action`, `Modal` | Original parent ID/customId, remix/mask/prompt and unknown fields |
| `submit/shorten`, `edits` | `SubmitAsync` with `Shorten`, `Edits` | Native text/image-edit task acknowledgement |
| `insight-face/swap` | `SubmitAsync` with `SwapFace` | Native image swap task acknowledgement |
| `submit/upload-discord-images` | `UploadInputsAsync` | Original image-input URL array and full metadata; no task ID conversion |
| `task/{id}/fetch` | `GetAsync` | One original task snapshot |
| `task/list-by-condition` | `GetManyAsync` | Original array order, associated by actual task ID, with missing tasks left missing |
| `task/{id}/image-seed` | `RequestImageSeedAsync` | Original seed or queued seed-request response; no automatic follow-up |

These are server-version-specific proxy contracts, not a Midjourney-hosted public API. Classic change/simple-change are documented in the [classic proxy API](https://github.com/novicezk/midjourney-proxy/blob/main/docs/api.md); the newer [submit controller](https://github.com/trueai-org/midjourney-proxy/blob/b68b2baa6667fa14306e521205ff687f9e10d83f/src/Midjourney.API/Controllers/SubmitController.cs), [task controller](https://github.com/trueai-org/midjourney-proxy/blob/b68b2baa6667fa14306e521205ff687f9e10d83f/src/Midjourney.API/Controllers/TaskController.cs) and [image-swap controller](https://github.com/trueai-org/midjourney-proxy/blob/b68b2baa6667fa14306e521205ff687f9e10d83f/src/Midjourney.API/Controllers/InsightFaceController.cs) define other supported native calls. Enablement, native bot/model support, account permissions and operation availability belong to the selected proxy. No server discovery, fallback endpoint, speed selection, model alias or protocol migration is automatic.

## Initialization and native submission

```csharp
using Asgard.Loki.Image.Midjourney;
using System.Text.Json.Nodes;

var images = new MidjourneyImageClient(httpClientFactory, new()
{
    Id = "midjourney-images",
    // Complete API root, including mj/ or the explicit speed-mode prefix.
    BaseAddress = proxyApiRoot,
    ApiSecret = proxyImageApiSecret // null explicitly selects anonymous calls.
});
// The application owns the factory; disable retries, hedging and redirects.
var submitted = await images.SubmitAsync(MidjourneyImageOperation.Imagine,
    new JsonObject
    {
        ["prompt"] = "A paper boat --ar 3:2 --seed 0",
        ["botType"] = "MID_JOURNEY",
        ["state"] = "application-owned-correlation",
        ["notifyHook"] = applicationProvidedCallbackUrl
    }, cancellationToken);
// Persist submitted.Reference if the application needs later queries.
var current = await images.GetAsync(submitted.Reference, cancellationToken);
```

This native asynchronous client is used directly, like other supplier-native task APIs. It does not manufacture a synchronous `ImageGenerationResult` or add a common capability coordinator. The required HTTP/HTTPS root is caller-owned, for example `https://proxy.example/mj/` or `https://proxy.example/mj-fast/mj/`. Its scheme, prefix and port remain explicit. Authentication uses the original `mj-api-secret` only; `null` is anonymous, and a nonnull secret must be nonempty printable ASCII without whitespace. There is no Bearer-prefix stripping, login, credential discovery or reuse of another capability's key. Use a dedicated named HTTP client.

The enum chooses a concrete operation path. The full `JsonObject` is native request JSON and is frozen before factory creation/first await. Prompt flags, original botType, callback/state, Base64/data-URI/URL inputs, zero/false/null, exact integers and unknown fields remain unchanged. Parent taskId, button customId, native one-based image-selection index and simple-change content stay opaque. The SDK does not parse a button into a different operation, reindex a selection, translate/strip a prompt, inject a model or fetch/normalize media. The caller supplies the correct parent and index on its original target; access and accepted input shapes are enforced upstream.

## Original acknowledgement and task state

Task submissions accept native codes `1`, `21` and `22` with an original nonempty string `result` task ID. The code stays unchanged; code `23` remains a failure even if it contains a task ID. `MidjourneyImageSubmission.Status` is only an explicitly supplied `properties.status`; without it, Status is null. A queued/existing acknowledgement does not invent task status or generated images. `Data` retains the complete original properties and unknown fields. HTTP/API errors preserve actual status, complete original bytes and content type in `ImageUpstreamException`.

`GetAsync` requires the same original ID in the returned task. `MidjourneyImageTask.Status` remains native. Only exact `SUCCESS`, `FAILURE` and `CANCEL` are known terminal states. `MODAL`, unknown states and progress `100%` alone cannot imply completion. A native success with a nonempty failReason is rejected as contradictory; an explicitly returned VIDEO task is rejected by this image client. Description and shortened-text tasks can complete without any image URL. A present `ImageUrl` may refer to an input, preview or final image; no generated artifact is inferred from it. Original prompt text, buttons, masks, failure reasons, properties, timestamps and unknown values remain in full `Data`.

Each response includes `HttpResponse` with actual status, original bytes and content type. Batch responses retain the original array and order. Tasks are matched by their actual IDs rather than response positions; unrelated/duplicate returned IDs fail, while legitimately missing requested tasks remain absent. Every child task retains the whole original batch HTTP response separately from its own JSON item. No local task map, automatic polling, timeout-to-failure rule, callback receiver or media download is supplied.

## Persistent task binding and batch queries

```csharp
// Explicitly associate an already known native ID with this complete target.
// This does not check existence or grant permission.
var existing = images.ReferenceTask(applicationKnownNativeTaskId);
var tasks = await images.GetManyAsync(
    new[] { submitted.Reference, existing },
    cancellationToken: cancellationToken);
// tasks.Tasks follows native response order. Inspect each task.Reference.TaskId.
```

References bind the target ID, normalized full root, credential/anonymous identity, implementation, named client and byte limits. They can be serialized and used after reconstructing the same client; a changed target fails before HTTP IO. A reference contains no raw secret and is not an authorization token. Batch IDs come only from bound references; duplicate request IDs and case-insensitive ids overrides fail locally. Optional extra native batch parameters remain intact. `ReferenceTask` is an explicit caller association for existing native IDs; it does not discover or migrate tasks.

## Image inputs and seed requests

```csharp
var input = await images.UploadInputsAsync(new JsonObject
{
    ["base64Array"] = new JsonArray(originalFirstImageDataUri, originalSecondImageDataUri)
}, cancellationToken);
// input.Data["result"] is the original URL array; order remains unchanged.
var seedResponse = await images.RequestImageSeedAsync(submitted.Reference, cancellationToken);
// Inspect original Code/Data. A queued response can contain a seed-request ID.
```

Image-input upload is one explicit native JSON request. The original URL array and all metadata remain in `Data`; upload does not create a local task, submit another model operation, decode data or retrieve a URL. It adds no file list/get/delete service or multipart conversion. Native error code handling is separate from task-submission acceptance.

The native seed endpoint may initiate seed work at the proxy when a seed is not cached. `RequestImageSeedAsync` performs exactly that one caller-authorized request. It preserves code/result/properties without casting a returned seed into a task ID or a queued request into a completed seed. No second query, retry or automatic task/reference creation occurs.

## Limits and verification

Requests default to at most 64 MiB of complete UTF-8 JSON; raw responses/errors default to 16 MiB. These are configurable local transport bounds, not upstream acceptance guarantees. Strict JSON/Unicode, duplicate members, byte/declared-length limits, native ID association, EOF, cancellation and resource-disposal checks precede result delivery. Each call owns its HTTP request/client/response; the factory remains borrowed. Changed response targets or methods fail explicitly. There is no automatic retry, hedging, redirect policy, reconnect, failover or media-key forwarding in this package.

63 xUnit v3 offline cases use independent native fixtures and substituted HTTP. They cover every retained operation, exact request/result snapshots, parent/button/index/mask identity, upload/seed distinctions, known/unknown/text-only outcomes, persistent full-target binding, reordered/missing/invalid task batches, opaque ID escaping, raw errors and HTTP/byte/length/encoding/cancellation/disposal boundaries. **No real proxy/account/model, image output, input upload, seed generation or live availability has been verified.** No live-test credentials or environment variables are added.
